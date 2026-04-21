using System.Collections.Concurrent;
using System.Threading.Channels;
using WebCrawler.Application.Ports;
using WebCrawler.Application.Transport;
using WebCrawler.Domain.Crawl;
using WebCrawler.Domain.Robots;

namespace WebCrawler.Application.Crawl;

public sealed class CrawlSiteService
{
    private readonly IPageFetcher _pageFetcher;
    private readonly IRobotsPolicyProvider _robotsPolicyProvider;
    private readonly IHtmlDocumentParser _htmlDocumentParser;

    public CrawlSiteService(
        IPageFetcher pageFetcher,
        IRobotsPolicyProvider robotsPolicyProvider,
        IHtmlDocumentParser htmlDocumentParser)
    {
        _pageFetcher = pageFetcher;
        _robotsPolicyProvider = robotsPolicyProvider;
        _htmlDocumentParser = htmlDocumentParser;
    }

    public async Task<CrawlReport> CrawlAsync(string seedInput, CrawlerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var seedUrl = CrawlUrlRules.ParseSeed(seedInput);
        var robotsRules = await _robotsPolicyProvider.GetRulesAsync(seedUrl, cancellationToken);
        var channel = Channel.CreateUnbounded<CrawlWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        var visited = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var results = new ConcurrentBag<CrawlPage>();
        var state = new CrawlRuntimeState(seedUrl, options, robotsRules, channel, visited, results);

        state.TryReserve(seedUrl);
        state.IncrementOutstanding();
        state.Channel.Writer.TryWrite(new CrawlWorkItem(seedUrl, 1));

        var workers = Enumerable.Range(0, options.EffectiveWorkerCount())
            .Select(_ => RunWorkerAsync(state, cancellationToken))
            .ToArray();

        await Task.WhenAll(workers);

        var pages = results
            .OrderBy(static page => page.Url.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static page => page.RequestedUrl.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CrawlReport(seedUrl, pages);
    }

    private async Task RunWorkerAsync(CrawlRuntimeState state, CancellationToken cancellationToken)
    {
        await foreach (var workItem in state.Channel.Reader.ReadAllAsync(cancellationToken))
        {
            await ProcessAsync(workItem, state, cancellationToken);
        }
    }

    private async Task ProcessAsync(CrawlWorkItem workItem, CrawlRuntimeState state, CancellationToken cancellationToken)
    {
        var fetchOutcome = await FetchWithRedirectsAsync(workItem.Url, state.SeedUrl, state.Options, cancellationToken);

        if (fetchOutcome.ShouldRetry && workItem.Attempt < state.Options.MaxAttempts)
        {
            _ = RequeueAsync(workItem.NextAttempt(), state, cancellationToken);
            return;
        }

        var result = fetchOutcome.Kind switch
        {
            FetchOutcomeKind.Success => await BuildSuccessResultAsync(fetchOutcome, state, cancellationToken),
            FetchOutcomeKind.RedirectedOutOfScope => new CrawlPage(
                workItem.Url,
                fetchOutcome.FinalUrl,
                CrawlPageStatus.RedirectedOutOfScope,
                fetchOutcome.StatusCode,
                null,
                Array.Empty<Uri>(),
                "redirected-out-of-scope",
                fetchOutcome.RedirectChain),
            _ => new CrawlPage(
                workItem.Url,
                fetchOutcome.FinalUrl,
                CrawlPageStatus.Failed,
                fetchOutcome.StatusCode,
                null,
                Array.Empty<Uri>(),
                fetchOutcome.Error,
                fetchOutcome.RedirectChain)
        };

        state.Results.Add(result);
        state.CompleteOutstanding();
    }

    private async Task<CrawlPage> BuildSuccessResultAsync(FetchOutcome fetchOutcome, CrawlRuntimeState state, CancellationToken cancellationToken)
    {
        var discoveredLinks = new HashSet<Uri>(AbsoluteUriComparer.Instance);
        string? title = null;

        if (IsHtml(fetchOutcome.ContentType))
        {
            var parsedDocument = _htmlDocumentParser.Parse(fetchOutcome.Body);
            title = parsedDocument.Title;

            foreach (var href in parsedDocument.Hrefs)
            {
                if (!CrawlUrlRules.TryResolveLink(fetchOutcome.FinalUrl, href, out var resolvedLink))
                {
                    continue;
                }

                if (!CrawlUrlRules.IsSupportedScheme(resolvedLink))
                {
                    continue;
                }

                var scopeDecision = await EvaluateScopeAsync(resolvedLink, state.SeedUrl, state.Options, cancellationToken);

                if (!scopeDecision.IsInScope)
                {
                    continue;
                }

                resolvedLink = scopeDecision.CrawlUrl;

                if (!state.RobotsRules.IsAllowed(resolvedLink) || !discoveredLinks.Add(resolvedLink))
                {
                    continue;
                }

                if (state.TryReserve(resolvedLink))
                {
                    state.IncrementOutstanding();
                    state.Channel.Writer.TryWrite(new CrawlWorkItem(resolvedLink, 1));
                }
            }
        }

        return new CrawlPage(
            fetchOutcome.RequestedUrl,
            fetchOutcome.FinalUrl,
            CrawlPageStatus.Succeeded,
            fetchOutcome.StatusCode,
            title,
            discoveredLinks.OrderBy(static uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray(),
            null,
            fetchOutcome.RedirectChain);
    }

    private async Task RequeueAsync(CrawlWorkItem workItem, CrawlRuntimeState state, CancellationToken cancellationToken)
    {
        try
        {
            var delay = TimeSpan.FromMilliseconds(state.Options.BaseRetryDelay.TotalMilliseconds * workItem.Attempt);
            await Task.Delay(delay, cancellationToken);
            state.Channel.Writer.TryWrite(workItem);
        }
        catch (OperationCanceledException)
        {
            state.Results.Add(new CrawlPage(
                workItem.Url,
                workItem.Url,
                CrawlPageStatus.Failed,
                null,
                null,
                Array.Empty<Uri>(),
                "cancelled",
                new[] { workItem.Url }));
            state.CompleteOutstanding();
        }
    }

    private async Task<FetchOutcome> FetchWithRedirectsAsync(Uri requestedUrl, Uri seedUrl, CrawlerOptions options, CancellationToken cancellationToken)
    {
        var currentUrl = requestedUrl;
        var redirectChain = new List<Uri> { requestedUrl };

        for (var redirectCount = 0; redirectCount <= options.MaxRedirects; redirectCount++)
        {
            var singleFetch = await _pageFetcher.FetchAsync(currentUrl, FetchRequestMethod.Get, options.RequestTimeout, cancellationToken);

            if (singleFetch.Kind == SingleFetchResultKind.Timeout)
            {
                return FetchOutcome.TransientFailure(requestedUrl, currentUrl, "timeout", redirectChain);
            }

            if (singleFetch.Kind == SingleFetchResultKind.TransportError)
            {
                return FetchOutcome.TransientFailure(requestedUrl, currentUrl, "transport-error", redirectChain);
            }

            var statusCode = singleFetch.StatusCode ?? 0;

            if (CrawlUrlRules.IsRedirectStatus(statusCode))
            {
                if (singleFetch.RedirectLocation is null)
                {
                    return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, statusCode, "invalid-redirect", redirectChain);
                }

                currentUrl = CrawlUrlRules.ResolveRedirect(currentUrl, singleFetch.RedirectLocation);
                redirectChain.Add(currentUrl);

                if (!CrawlUrlRules.IsInScope(currentUrl, seedUrl))
                {
                    return FetchOutcome.RedirectedOutOfScope(requestedUrl, currentUrl, statusCode, redirectChain);
                }

                continue;
            }

            if (CrawlUrlRules.IsTransientStatus(statusCode))
            {
                return FetchOutcome.TransientFailure(requestedUrl, currentUrl, $"http-{statusCode}", redirectChain, statusCode);
            }

            if (statusCode < 200 || statusCode >= 300)
            {
                return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, statusCode, $"http-{statusCode}", redirectChain);
            }

            return FetchOutcome.Success(requestedUrl, currentUrl, statusCode, singleFetch.Body, singleFetch.ContentType, redirectChain);
        }

        return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, null, "redirect-limit-exceeded", redirectChain);
    }

    private async Task<ScopeDecision> EvaluateScopeAsync(Uri candidateUrl, Uri seedUrl, CrawlerOptions options, CancellationToken cancellationToken)
    {
        if (!CrawlUrlRules.IsSupportedScheme(candidateUrl))
        {
            return ScopeDecision.OutOfScope(candidateUrl);
        }

        if (!CrawlUrlRules.RequiresScopeProbe(candidateUrl, seedUrl))
        {
            return ScopeDecision.InScope(candidateUrl);
        }

        var probeResult = await ProbeFinalDestinationAsync(candidateUrl, options, cancellationToken);

        if (!probeResult.IsSuccess)
        {
            return ScopeDecision.OutOfScope(candidateUrl);
        }

        return CrawlUrlRules.HasSameScopeIdentity(probeResult.FinalUrl, seedUrl)
            ? ScopeDecision.InScope(probeResult.FinalUrl)
            : ScopeDecision.OutOfScope(probeResult.FinalUrl);
    }

    private async Task<ProbeOutcome> ProbeFinalDestinationAsync(Uri candidateUrl, CrawlerOptions options, CancellationToken cancellationToken)
    {
        var headOutcome = await FollowProbeRedirectsAsync(candidateUrl, FetchRequestMethod.Head, options, cancellationToken);

        if (headOutcome.Kind == ProbeOutcomeKind.Success)
        {
            return headOutcome;
        }

        if (headOutcome.Kind == ProbeOutcomeKind.UnsupportedHead)
        {
            return await FollowProbeRedirectsAsync(candidateUrl, FetchRequestMethod.Get, options, cancellationToken);
        }

        return headOutcome;
    }

    private async Task<ProbeOutcome> FollowProbeRedirectsAsync(Uri candidateUrl, FetchRequestMethod method, CrawlerOptions options, CancellationToken cancellationToken)
    {
        var currentUrl = candidateUrl;

        for (var redirectCount = 0; redirectCount <= options.MaxRedirects; redirectCount++)
        {
            var singleFetch = await _pageFetcher.FetchAsync(currentUrl, method, options.RequestTimeout, cancellationToken);

            if (singleFetch.Kind != SingleFetchResultKind.Response)
            {
                return ProbeOutcome.Unresolved(currentUrl);
            }

            var statusCode = singleFetch.StatusCode ?? 0;

            if (method == FetchRequestMethod.Head && statusCode is 405 or 501)
            {
                return ProbeOutcome.UnsupportedHead(currentUrl);
            }

            if (CrawlUrlRules.IsRedirectStatus(statusCode))
            {
                if (singleFetch.RedirectLocation is null)
                {
                    return ProbeOutcome.Unresolved(currentUrl);
                }

                currentUrl = CrawlUrlRules.ResolveRedirect(currentUrl, singleFetch.RedirectLocation);
                continue;
            }

            return ProbeOutcome.Success(currentUrl);
        }

        return ProbeOutcome.Unresolved(currentUrl);
    }

    private static bool IsHtml(string? contentType)
    {
        return string.IsNullOrWhiteSpace(contentType)
            || contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CrawlWorkItem(Uri Url, int Attempt)
    {
        public CrawlWorkItem NextAttempt() => this with { Attempt = Attempt + 1 };
    }

    private sealed class CrawlRuntimeState
    {
        private int _outstanding;

        public CrawlRuntimeState(
            Uri seedUrl,
            CrawlerOptions options,
            RobotsRules robotsRules,
            Channel<CrawlWorkItem> channel,
            ConcurrentDictionary<string, byte> visited,
            ConcurrentBag<CrawlPage> results)
        {
            SeedUrl = seedUrl;
            Options = options;
            RobotsRules = robotsRules;
            Channel = channel;
            Visited = visited;
            Results = results;
        }

        public Uri SeedUrl { get; }

        public CrawlerOptions Options { get; }

        public RobotsRules RobotsRules { get; }

        public Channel<CrawlWorkItem> Channel { get; }

        public ConcurrentDictionary<string, byte> Visited { get; }

        public ConcurrentBag<CrawlPage> Results { get; }

        public bool TryReserve(Uri url)
        {
            return Visited.TryAdd(url.AbsoluteUri, 0);
        }

        public void IncrementOutstanding()
        {
            Interlocked.Increment(ref _outstanding);
        }

        public void CompleteOutstanding()
        {
            if (Interlocked.Decrement(ref _outstanding) == 0)
            {
                Channel.Writer.TryComplete();
            }
        }
    }

    private sealed record FetchOutcome(
        FetchOutcomeKind Kind,
        Uri RequestedUrl,
        Uri FinalUrl,
        int? StatusCode,
        string Body,
        string? ContentType,
        string? Error,
        bool ShouldRetry,
        IReadOnlyList<Uri> RedirectChain)
    {
        public static FetchOutcome Success(Uri requestedUrl, Uri finalUrl, int statusCode, string body, string? contentType, IReadOnlyList<Uri> redirectChain)
        {
            return new FetchOutcome(FetchOutcomeKind.Success, requestedUrl, finalUrl, statusCode, body, contentType, null, false, redirectChain);
        }

        public static FetchOutcome TransientFailure(Uri requestedUrl, Uri finalUrl, string error, IReadOnlyList<Uri> redirectChain, int? statusCode = null)
        {
            return new FetchOutcome(FetchOutcomeKind.TransientFailure, requestedUrl, finalUrl, statusCode, string.Empty, null, error, true, redirectChain);
        }

        public static FetchOutcome TerminalFailure(Uri requestedUrl, Uri finalUrl, int? statusCode, string error, IReadOnlyList<Uri> redirectChain)
        {
            return new FetchOutcome(FetchOutcomeKind.TerminalFailure, requestedUrl, finalUrl, statusCode, string.Empty, null, error, false, redirectChain);
        }

        public static FetchOutcome RedirectedOutOfScope(Uri requestedUrl, Uri finalUrl, int? statusCode, IReadOnlyList<Uri> redirectChain)
        {
            return new FetchOutcome(FetchOutcomeKind.RedirectedOutOfScope, requestedUrl, finalUrl, statusCode, string.Empty, null, "redirected-out-of-scope", false, redirectChain);
        }
    }

    private enum FetchOutcomeKind
    {
        Success,
        TransientFailure,
        TerminalFailure,
        RedirectedOutOfScope
    }

    private sealed class AbsoluteUriComparer : IEqualityComparer<Uri>
    {
        public static AbsoluteUriComparer Instance { get; } = new();

        public bool Equals(Uri? x, Uri? y)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(x?.AbsoluteUri, y?.AbsoluteUri);
        }

        public int GetHashCode(Uri obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AbsoluteUri);
        }
    }

    private readonly record struct ScopeDecision(bool IsInScope, Uri CrawlUrl)
    {
        public static ScopeDecision InScope(Uri crawlUrl) => new(true, crawlUrl);

        public static ScopeDecision OutOfScope(Uri crawlUrl) => new(false, crawlUrl);
    }

    private readonly record struct ProbeOutcome(ProbeOutcomeKind Kind, Uri FinalUrl)
    {
        public bool IsSuccess => Kind == ProbeOutcomeKind.Success;

        public static ProbeOutcome Success(Uri finalUrl) => new(ProbeOutcomeKind.Success, finalUrl);

        public static ProbeOutcome UnsupportedHead(Uri finalUrl) => new(ProbeOutcomeKind.UnsupportedHead, finalUrl);

        public static ProbeOutcome Unresolved(Uri finalUrl) => new(ProbeOutcomeKind.Unresolved, finalUrl);
    }

    private enum ProbeOutcomeKind
    {
        Success,
        UnsupportedHead,
        Unresolved
    }
}
