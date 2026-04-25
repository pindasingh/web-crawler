using System.Collections.Concurrent;
using WebCrawler.Application.Ports;
using WebCrawler.Application.Transport;
using WebCrawler.Domain.Crawl;
using WebCrawler.Domain.Robots;

namespace WebCrawler.Application.Crawl;

public sealed class SiteService
{
    private readonly IPageFetcher _pageFetcher;
    private readonly IRobotsPolicyProvider _robotsPolicyProvider;
    private readonly IHtmlDocumentParser _htmlDocumentParser;

    public SiteService(
        IPageFetcher pageFetcher,
        IRobotsPolicyProvider robotsPolicyProvider,
        IHtmlDocumentParser htmlDocumentParser)
    {
        _pageFetcher = pageFetcher;
        _robotsPolicyProvider = robotsPolicyProvider;
        _htmlDocumentParser = htmlDocumentParser;
    }

    public async Task<Report> RunAsync(string seedInput, Options options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var seedUrl = UrlRules.ParseSeed(seedInput);
        var robotsRules = await _robotsPolicyProvider.GetRulesAsync(seedUrl, cancellationToken);
        var reservations = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var results = new ConcurrentBag<Page>();
        var state = new RuntimeState(seedUrl, options, robotsRules, new Queue(), reservations, results);

        state.TryReserve(seedUrl);
        state.IncrementOutstanding();
        state.Queue.Enqueue(new WorkItem(seedUrl, 0, 1));

        var workers = Enumerable.Range(0, options.EffectiveWorkerCount())
            .Select(_ => RunWorkerAsync(state, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            state.Queue.Complete();
        }

        var pages = results
            .OrderBy(static page => page.Url.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static page => page.RequestedUrl.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Report(seedUrl, pages);
    }

    private async Task RunWorkerAsync(RuntimeState state, CancellationToken cancellationToken)
    {
        await foreach (var workItem in state.Queue.ReadAllAsync(cancellationToken))
        {
            await ProcessAsync(workItem, state, cancellationToken);
        }
    }

    private async Task ProcessAsync(WorkItem workItem, RuntimeState state, CancellationToken cancellationToken)
    {
        var fetchOutcome = (await FetchWithRedirectsAsync(workItem, state, cancellationToken))
            .WithWorkItem(workItem);

        if (fetchOutcome.Kind == FetchOutcomeKind.DuplicateFinalUrl)
        {
            state.CompleteOutstanding();
            return;
        }

        if (fetchOutcome.ShouldRetry && workItem.Attempt < state.Options.MaxAttempts)
        {
            var delay = CalculateRetryDelay(fetchOutcome, state.Options, workItem.Attempt);
            state.Queue.Enqueue(workItem.NextAttempt(), DateTimeOffset.UtcNow + delay);
            return;
        }

        var result = fetchOutcome.Kind switch
        {
            FetchOutcomeKind.Success => await BuildSuccessResultAsync(fetchOutcome, state, cancellationToken),
            FetchOutcomeKind.RedirectedOutOfScope => new Page(
                workItem.Url,
                fetchOutcome.FinalUrl,
                PageStatus.RedirectedOutOfScope,
                fetchOutcome.StatusCode,
                null,
                Array.Empty<Uri>(),
                "redirected-out-of-scope",
                fetchOutcome.RedirectChain,
                workItem.Attempt),
            _ => new Page(
                workItem.Url,
                fetchOutcome.FinalUrl,
                PageStatus.Failed,
                fetchOutcome.StatusCode,
                null,
                Array.Empty<Uri>(),
                fetchOutcome.Error,
                fetchOutcome.RedirectChain,
                workItem.Attempt)
        };

        state.Results.Add(result);
        state.CompleteOutstanding();
    }

    private async Task<Page> BuildSuccessResultAsync(FetchOutcome fetchOutcome, RuntimeState state, CancellationToken cancellationToken)
    {
        var discoveredLinks = new HashSet<Uri>(AbsoluteUriComparer.Instance);
        string? title = null;

        if (IsHtml(fetchOutcome.ContentType))
        {
            var parsedDocument = _htmlDocumentParser.Parse(fetchOutcome.Body);
            title = parsedDocument.Title;

            foreach (var href in parsedDocument.Hrefs)
            {
                if (!UrlRules.TryResolveLink(fetchOutcome.FinalUrl, href, out var resolvedLink))
                {
                    continue;
                }

                if (!UrlRules.IsSupportedScheme(resolvedLink))
                {
                    continue;
                }

                var scopeDecision = await EvaluateScopeAsync(resolvedLink, state.SeedUrl, state.Options, cancellationToken);

                if (!scopeDecision.IsInScope)
                {
                    continue;
                }

                resolvedLink = scopeDecision.Url;

                if (!state.RobotsRules.IsAllowed(resolvedLink) || !discoveredLinks.Add(resolvedLink))
                {
                    continue;
                }

                if (!CanVisitDepth(fetchOutcome.Depth + 1, state.Options))
                {
                    continue;
                }

                if (state.TryReserve(resolvedLink))
                {
                    state.IncrementOutstanding();
                    state.Queue.Enqueue(new WorkItem(resolvedLink, fetchOutcome.Depth + 1, 1));
                }
            }
        }

        return new Page(
            fetchOutcome.RequestedUrl,
            fetchOutcome.FinalUrl,
            PageStatus.Succeeded,
            fetchOutcome.StatusCode,
            title,
            discoveredLinks.OrderBy(static uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray(),
            null,
            fetchOutcome.RedirectChain,
            fetchOutcome.Attempt);
    }

    private static TimeSpan CalculateRetryDelay(FetchOutcome fetchOutcome, Options options, int failedAttempt)
    {
        if (fetchOutcome.StatusCode is 429 or 503 && fetchOutcome.RetryAfter is { } retryAfter)
        {
            return retryAfter;
        }

        if (options.BaseRetryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Max(0, failedAttempt - 1));
        var baseDelayMs = options.BaseRetryDelay.TotalMilliseconds * multiplier;
        var jitterMs = Random.Shared.NextDouble() * options.BaseRetryDelay.TotalMilliseconds * 0.25;
        return TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
    }

    private async Task<FetchOutcome> FetchWithRedirectsAsync(WorkItem workItem, RuntimeState state, CancellationToken cancellationToken)
    {
        var requestedUrl = workItem.Url;
        var currentUrl = requestedUrl;
        var redirectChain = new List<Uri> { requestedUrl };

        for (var redirectCount = 0; redirectCount <= state.Options.MaxRedirects; redirectCount++)
        {
            var singleFetch = await _pageFetcher.FetchAsync(currentUrl, FetchRequestMethod.Get, state.Options.RequestTimeout, state.Options.MaxPageBytes, cancellationToken);

            if (singleFetch.Kind == SingleFetchResultKind.Timeout)
            {
                return FetchOutcome.TransientFailure(requestedUrl, currentUrl, "timeout", redirectChain);
            }

            if (singleFetch.Kind == SingleFetchResultKind.TransportError)
            {
                return FetchOutcome.TransientFailure(requestedUrl, currentUrl, "transport-error", redirectChain);
            }

            var statusCode = singleFetch.StatusCode ?? 0;

            if (singleFetch.Kind == SingleFetchResultKind.ResponseTooLarge)
            {
                return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, statusCode, "response-too-large", redirectChain);
            }

            if (UrlRules.IsRedirectStatus(statusCode))
            {
                if (singleFetch.RedirectLocation is null)
                {
                    return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, statusCode, "invalid-redirect", redirectChain);
                }

                currentUrl = UrlRules.ResolveRedirect(currentUrl, singleFetch.RedirectLocation);

                if (redirectChain.Contains(currentUrl, AbsoluteUriComparer.Instance))
                {
                    redirectChain.Add(currentUrl);
                    return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, statusCode, "redirect-loop", redirectChain);
                }

                redirectChain.Add(currentUrl);

                if (!UrlRules.IsInScope(currentUrl, state.SeedUrl))
                {
                    return FetchOutcome.RedirectedOutOfScope(requestedUrl, currentUrl, statusCode, redirectChain);
                }

                if (!state.TryReserveRedirectTarget(workItem, currentUrl))
                {
                    return FetchOutcome.DuplicateFinalUrl(requestedUrl, currentUrl, statusCode, redirectChain);
                }

                continue;
            }

            if (UrlRules.IsTransientStatus(statusCode))
            {
                return FetchOutcome.TransientFailure(requestedUrl, currentUrl, $"http-{statusCode}", redirectChain, statusCode, singleFetch.RetryAfter);
            }

            if (statusCode < 200 || statusCode >= 300)
            {
                return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, statusCode, $"http-{statusCode}", redirectChain);
            }

            return FetchOutcome.Success(requestedUrl, currentUrl, statusCode, singleFetch.Body, singleFetch.ContentType, redirectChain);
        }

        return FetchOutcome.TerminalFailure(requestedUrl, currentUrl, null, "redirect-limit-exceeded", redirectChain);
    }

    private async Task<ScopeDecision> EvaluateScopeAsync(Uri candidateUrl, Uri seedUrl, Options options, CancellationToken cancellationToken)
    {
        if (!UrlRules.IsSupportedScheme(candidateUrl))
        {
            return ScopeDecision.OutOfScope(candidateUrl);
        }

        if (!UrlRules.RequiresScopeProbe(candidateUrl, seedUrl))
        {
            return ScopeDecision.InScope(candidateUrl);
        }

        var probeResult = await ProbeFinalDestinationAsync(candidateUrl, options, cancellationToken);

        if (!probeResult.IsSuccess)
        {
            return ScopeDecision.OutOfScope(candidateUrl);
        }

        return UrlRules.HasSameScopeIdentity(probeResult.FinalUrl, seedUrl)
            ? ScopeDecision.InScope(probeResult.FinalUrl)
            : ScopeDecision.OutOfScope(probeResult.FinalUrl);
    }

    private async Task<ProbeOutcome> ProbeFinalDestinationAsync(Uri candidateUrl, Options options, CancellationToken cancellationToken)
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

    private async Task<ProbeOutcome> FollowProbeRedirectsAsync(Uri candidateUrl, FetchRequestMethod method, Options options, CancellationToken cancellationToken)
    {
        var currentUrl = candidateUrl;

        for (var redirectCount = 0; redirectCount <= options.MaxRedirects; redirectCount++)
        {
            var singleFetch = await _pageFetcher.FetchAsync(currentUrl, method, options.RequestTimeout, options.MaxPageBytes, cancellationToken);

            if (singleFetch.Kind != SingleFetchResultKind.Response)
            {
                return ProbeOutcome.Unresolved(currentUrl);
            }

            var statusCode = singleFetch.StatusCode ?? 0;

            if (method == FetchRequestMethod.Head && statusCode is 405 or 501)
            {
                return ProbeOutcome.UnsupportedHead(currentUrl);
            }

            if (UrlRules.IsRedirectStatus(statusCode))
            {
                if (singleFetch.RedirectLocation is null)
                {
                    return ProbeOutcome.Unresolved(currentUrl);
                }

                currentUrl = UrlRules.ResolveRedirect(currentUrl, singleFetch.RedirectLocation);

                if (!UrlRules.IsSupportedScheme(currentUrl))
                {
                    return ProbeOutcome.Unresolved(currentUrl);
                }

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

    private static bool CanVisitDepth(int depth, Options options)
    {
        return options.MaxDepth is null || depth <= options.MaxDepth;
    }

    private sealed class RuntimeState
    {
        private int _outstanding;

        public RuntimeState(
            Uri seedUrl,
            Options options,
            RobotsRules robotsRules,
            Queue queue,
            ConcurrentDictionary<string, string> reservations,
            ConcurrentBag<Page> results)
        {
            SeedUrl = seedUrl;
            Options = options;
            RobotsRules = robotsRules;
            Queue = queue;
            Reservations = reservations;
            Results = results;
        }

        public Uri SeedUrl { get; }

        public Options Options { get; }

        public RobotsRules RobotsRules { get; }

        public Queue Queue { get; }

        public ConcurrentDictionary<string, string> Reservations { get; }

        public ConcurrentBag<Page> Results { get; }

        public bool TryReserve(Uri url)
        {
            return Reservations.TryAdd(url.AbsoluteUri, url.AbsoluteUri);
        }

        public bool TryReserveRedirectTarget(WorkItem workItem, Uri finalUrl)
        {
            if (AbsoluteUriComparer.Instance.Equals(workItem.Url, finalUrl))
            {
                return true;
            }

            var owner = workItem.Url.AbsoluteUri;

            // The value tracks the requested URL that owns a final redirect reservation.
            if (!Reservations.TryAdd(finalUrl.AbsoluteUri, owner))
            {
                return Reservations.TryGetValue(finalUrl.AbsoluteUri, out var existingOwner)
                    && StringComparer.OrdinalIgnoreCase.Equals(existingOwner, owner);
            }

            return true;
        }

        public void IncrementOutstanding()
        {
            Interlocked.Increment(ref _outstanding);
        }

        public void CompleteOutstanding()
        {
            if (Interlocked.Decrement(ref _outstanding) == 0)
            {
                Queue.Complete();
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
        IReadOnlyList<Uri> RedirectChain,
        TimeSpan? RetryAfter,
        int Depth = 0,
        int Attempt = 1)
    {
        public static FetchOutcome Success(Uri requestedUrl, Uri finalUrl, int statusCode, string body, string? contentType, IReadOnlyList<Uri> redirectChain)
        {
            return new FetchOutcome(FetchOutcomeKind.Success, requestedUrl, finalUrl, statusCode, body, contentType, null, false, redirectChain, null);
        }

        public static FetchOutcome TransientFailure(Uri requestedUrl, Uri finalUrl, string error, IReadOnlyList<Uri> redirectChain, int? statusCode = null, TimeSpan? retryAfter = null)
        {
            return new FetchOutcome(FetchOutcomeKind.TransientFailure, requestedUrl, finalUrl, statusCode, string.Empty, null, error, true, redirectChain, retryAfter);
        }

        public static FetchOutcome TerminalFailure(Uri requestedUrl, Uri finalUrl, int? statusCode, string error, IReadOnlyList<Uri> redirectChain)
        {
            return new FetchOutcome(FetchOutcomeKind.TerminalFailure, requestedUrl, finalUrl, statusCode, string.Empty, null, error, false, redirectChain, null);
        }

        public static FetchOutcome RedirectedOutOfScope(Uri requestedUrl, Uri finalUrl, int? statusCode, IReadOnlyList<Uri> redirectChain)
        {
            return new FetchOutcome(FetchOutcomeKind.RedirectedOutOfScope, requestedUrl, finalUrl, statusCode, string.Empty, null, "redirected-out-of-scope", false, redirectChain, null);
        }

        public static FetchOutcome DuplicateFinalUrl(Uri requestedUrl, Uri finalUrl, int? statusCode, IReadOnlyList<Uri> redirectChain)
        {
            return new FetchOutcome(FetchOutcomeKind.DuplicateFinalUrl, requestedUrl, finalUrl, statusCode, string.Empty, null, "duplicate-final-url", false, redirectChain, null);
        }

        public FetchOutcome WithWorkItem(WorkItem workItem) => this with { Depth = workItem.Depth, Attempt = workItem.Attempt };
    }

    private enum FetchOutcomeKind
    {
        Success,
        TransientFailure,
        TerminalFailure,
        RedirectedOutOfScope,
        DuplicateFinalUrl
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

    private readonly record struct ScopeDecision(bool IsInScope, Uri Url)
    {
        public static ScopeDecision InScope(Uri url) => new(true, url);

        public static ScopeDecision OutOfScope(Uri url) => new(false, url);
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
