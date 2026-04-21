using System.Collections.Concurrent;
using WebCrawler.Application.Crawl;
using WebCrawler.Application.Ports;
using WebCrawler.Application.Transport;
using WebCrawler.Domain.Crawl;
using WebCrawler.Domain.Robots;
using WebCrawler.Infrastructure.Html;

namespace WebCrawler.Cli.Tests;

public class CrawlDomainConventionTests
{
    // AC-001
    [Fact]
    public async Task GivenHttpAndHttpsVariants_WhenCrawlEvaluatesScope_ThenTheyShareTheSameScopeFamily()
    {
        var fetcher = new FakePageFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"http://example.com/about\">About</a><a href=\"ftp://example.com/file\">Ftp</a></body></html>");
        fetcher.EnqueueGetResponse("http://example.com/about", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        var report = await service.CrawlAsync("https://example.com/", new CrawlerOptions { WorkerCount = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "http://example.com/about");
        Assert.Equal(0, fetcher.GetRequestCount("ftp://example.com/file", FetchRequestMethod.Get));
    }

    // AC-002
    [Fact]
    public void GivenHostCaseVariants_WhenComparingScopeIdentity_ThenTheyMatch()
    {
        var upper = new Uri("https://Example.COM/path");
        var lower = new Uri("https://example.com/other");

        Assert.True(CrawlUrlRules.HasSameScopeIdentity(upper, lower));
    }

    // AC-003
    [Fact]
    public void GivenDefaultPorts_WhenComparingScopeIdentity_ThenTheyMatch()
    {
        var http = new Uri("http://example.com:80/");
        var https = new Uri("https://example.com/");

        Assert.True(CrawlUrlRules.HasSameScopeIdentity(http, https));
    }

    // AC-004
    [Fact]
    public void GivenUnicodeAndPunycodeHosts_WhenComparingScopeIdentity_ThenTheyMatch()
    {
        var unicode = new Uri("https://bücher.example/");
        var punycode = new Uri("https://xn--bcher-kva.example/");

        Assert.True(CrawlUrlRules.HasSameScopeIdentity(unicode, punycode));
    }

    // AC-005
    [Fact]
    public async Task GivenHeadUnsupportedProbe_WhenEvaluatingVariantScope_ThenItFallsBackToGet()
    {
        var fetcher = new FakePageFetcher();
        fetcher.EnqueueGetResponse("https://www.example.com/", 200, "<html><body><a href=\"https://example.com/entry\">Entry</a></body></html>");
        fetcher.EnqueueHeadResponse("https://example.com/entry", 405, string.Empty, "text/plain");
        fetcher.EnqueueGetResponse("https://example.com/entry", 302, string.Empty, "text/plain", "https://www.example.com/landing");
        fetcher.EnqueueGetResponse("https://www.example.com/landing", 200, string.Empty, "text/html");
        fetcher.EnqueueGetResponse("https://www.example.com/landing", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        var report = await service.CrawlAsync("https://www.example.com/", new CrawlerOptions { WorkerCount = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://www.example.com/landing");
        Assert.Equal(1, fetcher.GetRequestCount("https://example.com/entry", FetchRequestMethod.Head));
        Assert.Equal(1, fetcher.GetRequestCount("https://example.com/entry", FetchRequestMethod.Get));
        Assert.Contains("HEAD https://example.com/entry", fetcher.RequestOrder);
        Assert.Contains("GET https://example.com/entry", fetcher.RequestOrder);
    }

    // AC-006
    [Fact]
    public async Task GivenWwwAndNonWwwVariants_WhenNoResolvedMatchExists_ThenTheyRemainDistinct()
    {
        var fetcher = new FakePageFetcher();
        fetcher.EnqueueGetResponse("https://www.example.com/", 200, "<html><body><a href=\"https://example.com/plain\">Plain</a></body></html>");
        fetcher.EnqueueHeadResponse("https://example.com/plain", 200, string.Empty, "text/plain");

        var service = CreateService(fetcher);

        var report = await service.CrawlAsync("https://www.example.com/", new CrawlerOptions { WorkerCount = 1 }, CancellationToken.None);

        Assert.DoesNotContain(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/plain");
        Assert.Equal(0, fetcher.GetRequestCount("https://example.com/plain", FetchRequestMethod.Get));
    }

    // AC-007
    // AC-008
    [Fact]
    public async Task GivenVariantRedirectsToSeedIdentity_WhenEvaluatingScope_ThenResolvedDestinationControlsCrawlEligibility()
    {
        var fetcher = new FakePageFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"https://www.example.com/entry\">Entry</a><a href=\"https://example.com./dot\">Dot</a></body></html>");
        fetcher.EnqueueHeadResponse("https://www.example.com/entry", 302, string.Empty, "text/plain", "https://example.com/final");
        fetcher.EnqueueHeadResponse("https://example.com/final", 200, string.Empty, "text/plain");
        fetcher.EnqueueHeadResponse("https://example.com./dot", 302, string.Empty, "text/plain", "https://example.com/dot-final");
        fetcher.EnqueueHeadResponse("https://example.com/dot-final", 200, string.Empty, "text/plain");
        fetcher.EnqueueGetResponse("https://example.com/final", 200, "<html><body></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/dot-final", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        var report = await service.CrawlAsync("https://example.com/", new CrawlerOptions { WorkerCount = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/final");
        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/dot-final");
        Assert.DoesNotContain(report.Pages, static page => page.Url.AbsoluteUri == "https://www.example.com/entry");
    }

    // AC-009
    [Fact]
    public async Task GivenWwwVariantRedirectsToDifferentIdentity_WhenEvaluatingScope_ThenItDoesNotMatchTheSeed()
    {
        var fetcher = new FakePageFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"https://www.example.com/entry\">Entry</a></body></html>");
        fetcher.EnqueueHeadResponse("https://www.example.com/entry", 302, string.Empty, "text/plain", "https://www.example.com/final");
        fetcher.EnqueueHeadResponse("https://www.example.com/final", 200, string.Empty, "text/plain");

        var service = CreateService(fetcher);

        var report = await service.CrawlAsync("https://example.com/", new CrawlerOptions { WorkerCount = 1 }, CancellationToken.None);

        Assert.DoesNotContain(report.Pages, static page => page.Url.AbsoluteUri == "https://www.example.com/final");
        Assert.Equal(0, fetcher.GetRequestCount("https://www.example.com/final", FetchRequestMethod.Get));
    }

    private static CrawlSiteService CreateService(FakePageFetcher fetcher)
    {
        return new CrawlSiteService(
            fetcher,
            new FakeRobotsPolicyProvider(RobotsRules.AllowAll()),
            new HtmlAgilityDocumentParser());
    }

    private sealed class FakePageFetcher : IPageFetcher
    {
        private readonly Dictionary<string, Queue<Func<SingleFetchResult>>> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _requestCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly ConcurrentQueue<string> _requestOrder = new();

        public string RequestOrder => string.Join("|", _requestOrder);

        public void EnqueueGetResponse(string url, int statusCode, string body, string contentType = "text/html", string? location = null)
        {
            EnqueueResponse(url, FetchRequestMethod.Get, statusCode, body, contentType, location);
        }

        public void EnqueueHeadResponse(string url, int statusCode, string body, string contentType = "text/plain", string? location = null)
        {
            EnqueueResponse(url, FetchRequestMethod.Head, statusCode, body, contentType, location);
        }

        public int GetRequestCount(string url, FetchRequestMethod method)
        {
            lock (_lock)
            {
                var key = CreateKey(url, method);
                return _requestCounts.TryGetValue(key, out var count) ? count : 0;
            }
        }

        public Task<SingleFetchResult> FetchAsync(Uri url, FetchRequestMethod method, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var key = CreateKey(url.AbsoluteUri, method);

            lock (_lock)
            {
                _requestCounts[key] = GetRequestCount(url.AbsoluteUri, method) + 1;
            }

            _requestOrder.Enqueue($"{method.ToString().ToUpperInvariant()} {url.AbsoluteUri}");

            if (!_responses.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"No fake response queued for {method} {url.AbsoluteUri}.");
            }

            return Task.FromResult(queue.Dequeue().Invoke());
        }

        private void EnqueueResponse(string url, FetchRequestMethod method, int statusCode, string body, string contentType, string? location)
        {
            GetQueue(url, method).Enqueue(() => SingleFetchResult.Response(
                statusCode,
                body,
                contentType,
                location is null ? null : new Uri(location, UriKind.RelativeOrAbsolute)));
        }

        private Queue<Func<SingleFetchResult>> GetQueue(string url, FetchRequestMethod method)
        {
            lock (_lock)
            {
                var key = CreateKey(url, method);

                if (!_responses.TryGetValue(key, out var queue))
                {
                    queue = new Queue<Func<SingleFetchResult>>();
                    _responses[key] = queue;
                }

                return queue;
            }
        }

        private static string CreateKey(string url, FetchRequestMethod method)
        {
            return $"{method}:{url}";
        }
    }

    private sealed class FakeRobotsPolicyProvider : IRobotsPolicyProvider
    {
        private readonly RobotsRules _robotsRules;

        public FakeRobotsPolicyProvider(RobotsRules robotsRules)
        {
            _robotsRules = robotsRules;
        }

        public Task<RobotsRules> GetRulesAsync(Uri seedUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult(_robotsRules);
        }
    }
}
