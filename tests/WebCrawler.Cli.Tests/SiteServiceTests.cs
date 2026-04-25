using System.Collections.Concurrent;
using System.Text;
using WebCrawler.Application.Crawl;
using WebCrawler.Application.Ports;
using WebCrawler.Application.Transport;
using WebCrawler.Domain.Crawl;
using WebCrawler.Domain.Robots;
using WebCrawler.Infrastructure.Html;

namespace WebCrawler.Cli.Tests;

public class SiteServiceTests
{
    [Fact]
    public async Task IncludesHttpAndHttpsVariantsForSameScopeIdentity()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"http://example.com/about\">About</a><a href=\"ftp://example.com/file\">Ftp</a></body></html>");
        fetcher.EnqueueGetResponse("http://example.com/about", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "http://example.com/about");
        Assert.Equal(0, fetcher.GetRequestCount("ftp://example.com/file", HttpFetchMethod.Get));
    }

    [Fact]
    public void TreatsHostCaseVariantsAsSameScopeIdentity()
    {
        var upper = new Uri("https://Example.COM/path");
        var lower = new Uri("https://example.com/other");

        Assert.True(UrlRules.HasSameScopeIdentity(upper, lower));
    }

    [Fact]
    public void TreatsDefaultPortsAsSameScopeIdentity()
    {
        var http = new Uri("http://example.com:80/");
        var https = new Uri("https://example.com/");

        Assert.True(UrlRules.HasSameScopeIdentity(http, https));
    }

    [Fact]
    public void TreatsUnicodeAndPunycodeHostsAsSameScopeIdentity()
    {
        var unicode = new Uri("https://bücher.example/");
        var punycode = new Uri("https://xn--bcher-kva.example/");

        Assert.True(UrlRules.HasSameScopeIdentity(unicode, punycode));
    }

    [Fact]
    public async Task FallsBackToGetWhenHeadProbeIsUnsupported()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://www.example.com/", 200, "<html><body><a href=\"https://example.com/entry\">Entry</a></body></html>");
        fetcher.EnqueueHeadResponse("https://example.com/entry", 405, string.Empty, "text/plain");
        fetcher.EnqueueGetResponse("https://example.com/entry", 302, string.Empty, "text/plain", "https://www.example.com/landing");
        fetcher.EnqueueGetResponse("https://www.example.com/landing", 200, string.Empty, "text/html");
        fetcher.EnqueueGetResponse("https://www.example.com/landing", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://www.example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://www.example.com/landing");
        Assert.Equal(1, fetcher.GetRequestCount("https://example.com/entry", HttpFetchMethod.Head));
        Assert.Equal(1, fetcher.GetRequestCount("https://example.com/entry", HttpFetchMethod.Get));
        Assert.Contains("HEAD https://example.com/entry", fetcher.RequestOrder);
        Assert.Contains("GET https://example.com/entry", fetcher.RequestOrder);
    }

    [Fact]
    public async Task KeepsWwwAndNonWwwDistinctWhenRedirectProbeDoesNotResolveToSeedIdentity()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://www.example.com/", 200, "<html><body><a href=\"https://example.com/plain\">Plain</a></body></html>");
        fetcher.EnqueueHeadResponse("https://example.com/plain", 200, string.Empty, "text/plain");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://www.example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        Assert.DoesNotContain(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/plain");
        Assert.Equal(0, fetcher.GetRequestCount("https://example.com/plain", HttpFetchMethod.Get));
    }

    [Fact]
    public async Task UsesResolvedDestinationForVariantScopeEligibility()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"https://www.example.com/entry\">Entry</a><a href=\"https://example.com./dot\">Dot</a></body></html>");
        fetcher.EnqueueHeadResponse("https://www.example.com/entry", 302, string.Empty, "text/plain", "https://example.com/final");
        fetcher.EnqueueHeadResponse("https://example.com/final", 200, string.Empty, "text/plain");
        fetcher.EnqueueHeadResponse("https://example.com./dot", 302, string.Empty, "text/plain", "https://example.com/dot-final");
        fetcher.EnqueueHeadResponse("https://example.com/dot-final", 200, string.Empty, "text/plain");
        fetcher.EnqueueGetResponse("https://example.com/final", 200, "<html><body></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/dot-final", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/final");
        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/dot-final");
        Assert.DoesNotContain(report.Pages, static page => page.Url.AbsoluteUri == "https://www.example.com/entry");
    }

    [Fact]
    public async Task RejectsVariantRedirectsOutsideSeedIdentity()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"https://www.example.com/entry\">Entry</a></body></html>");
        fetcher.EnqueueHeadResponse("https://www.example.com/entry", 302, string.Empty, "text/plain", "https://www.example.com/final");
        fetcher.EnqueueHeadResponse("https://www.example.com/final", 200, string.Empty, "text/plain");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        Assert.DoesNotContain(report.Pages, static page => page.Url.AbsoluteUri == "https://www.example.com/final");
        Assert.Equal(0, fetcher.GetRequestCount("https://www.example.com/final", HttpFetchMethod.Get));
    }

    [Fact]
    public async Task FetchesSharedFinalRedirectTargetOnce()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"/a\">A</a><a href=\"/b\">B</a></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/a", 302, string.Empty, "text/plain", "https://example.com/final");
        fetcher.EnqueueGetResponse("https://example.com/final", 200, "<html><body></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/b", 302, string.Empty, "text/plain", "https://example.com/final");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        Assert.Single(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/final");
        Assert.Equal(1, fetcher.GetRequestCount("https://example.com/final", HttpFetchMethod.Get));
    }

    [Fact]
    public async Task ReportsInScopeRedirectsOutOfScope()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"/exit\">Exit</a></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/exit", 302, string.Empty, "text/plain", "https://other.example/final");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        var page = Assert.Single(report.Pages, static page => page.RequestedUrl.AbsoluteUri == "https://example.com/exit");
        Assert.Equal(PageStatus.RedirectedOutOfScope, page.Status);
        Assert.Equal("redirected-out-of-scope", page.Error);
        Assert.Equal("https://other.example/final", page.Url.AbsoluteUri);
    }

    [Fact]
    public async Task FailsRedirectLoopsWithoutUnboundedFetches()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 302, string.Empty, "text/plain", "https://example.com/a");
        fetcher.EnqueueGetResponse("https://example.com/a", 302, string.Empty, "text/plain", "https://example.com/");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        var page = Assert.Single(report.Pages);
        Assert.Equal(PageStatus.Failed, page.Status);
        Assert.Equal("redirect-loop", page.Error);
        Assert.Equal(1, fetcher.GetRequestCount("https://example.com/", HttpFetchMethod.Get));
        Assert.Equal(1, fetcher.GetRequestCount("https://example.com/a", HttpFetchMethod.Get));
    }

    [Fact]
    public async Task ReportsFinalAttemptWhenRetryAttemptsAreExhausted()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 503, string.Empty, retryAfter: TimeSpan.Zero);
        fetcher.EnqueueGetResponse("https://example.com/", 503, string.Empty, retryAfter: TimeSpan.Zero);
        fetcher.EnqueueGetResponse("https://example.com/", 503, string.Empty, retryAfter: TimeSpan.Zero);

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1, BaseRetryDelay = TimeSpan.Zero }, CancellationToken.None);

        var page = Assert.Single(report.Pages);
        Assert.Equal(PageStatus.Failed, page.Status);
        Assert.Equal("http-503", page.Error);
        Assert.Equal(503, page.StatusCode);
        Assert.Equal(3, page.AttemptCount);
        Assert.Equal(3, fetcher.GetRequestCount("https://example.com/", HttpFetchMethod.Get));
    }

    [Fact]
    public async Task ProcessesReadyWorkBeforeDelayedRetry()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"/retry\">Retry</a><a href=\"/other\">Other</a></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/retry", 500, string.Empty);
        fetcher.EnqueueGetResponse("https://example.com/retry", 200, "<html><body></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/other", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        await service.RunAsync("https://example.com/", new Options { WorkerCount = 1, BaseRetryDelay = TimeSpan.FromMilliseconds(1) }, CancellationToken.None);

        Assert.Contains(
            "GET https://example.com/retry|GET https://example.com/other|GET https://example.com/retry",
            fetcher.RequestOrder);
    }

    [Fact]
    public async Task ReportsOversizedResponsesWithoutParsingLinks()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"/hidden\">Hidden</a></body></html>");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1, MaxPageBytes = 10 }, CancellationToken.None);

        var page = Assert.Single(report.Pages);
        Assert.Equal(PageStatus.Failed, page.Status);
        Assert.Equal("response-too-large", page.Error);
        Assert.Equal(0, fetcher.GetRequestCount("https://example.com/hidden", HttpFetchMethod.Get));
    }

    [Fact]
    public async Task DoesNotFetchLinksBeyondConfiguredMaxDepth()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"/child\">Child</a></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/child", 200, "<html><body><a href=\"/grandchild\">Grandchild</a></body></html>");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1, MaxDepth = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/child");
        Assert.DoesNotContain(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/grandchild");
        Assert.Equal(0, fetcher.GetRequestCount("https://example.com/grandchild", HttpFetchMethod.Get));
    }

    [Fact]
    public async Task UsesUnlimitedDepthByDefault()
    {
        var fetcher = new FakeHttpResourceFetcher();
        fetcher.EnqueueGetResponse("https://example.com/", 200, "<html><body><a href=\"/child\">Child</a></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/child", 200, "<html><body><a href=\"/grandchild\">Grandchild</a></body></html>");
        fetcher.EnqueueGetResponse("https://example.com/grandchild", 200, "<html><body></body></html>");

        var service = CreateService(fetcher);

        var report = await service.RunAsync("https://example.com/", new Options { WorkerCount = 1 }, CancellationToken.None);

        Assert.Contains(report.Pages, static page => page.Url.AbsoluteUri == "https://example.com/grandchild");
    }

    [Fact]
    public void IgnoresRobotsSitemapDirectives()
    {
        var rules = RobotsRules.Parse("User-agent: *\nSitemap: https://example.com/sitemap.xml\nDisallow: /blocked");

        Assert.True(rules.IsAllowed(new Uri("https://example.com/sitemap.xml")));
        Assert.False(rules.IsAllowed(new Uri("https://example.com/blocked")));
    }

    private static SiteService CreateService(FakeHttpResourceFetcher fetcher)
    {
        return new SiteService(
            fetcher,
            new FakeRobotsPolicyProvider(RobotsRules.AllowAll()),
            new HtmlAgilityDocumentParser());
    }

    private sealed class FakeHttpResourceFetcher : IHttpResourceFetcher
    {
        private readonly Dictionary<string, Queue<Func<int, HttpFetchResult>>> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _requestCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly ConcurrentQueue<string> _requestOrder = new();

        public string RequestOrder => string.Join("|", _requestOrder);

        public void EnqueueGetResponse(string url, int statusCode, string body, string contentType = "text/html", string? location = null, TimeSpan? retryAfter = null)
        {
            EnqueueResponse(url, HttpFetchMethod.Get, statusCode, body, contentType, location, retryAfter);
        }

        public void EnqueueHeadResponse(string url, int statusCode, string body, string contentType = "text/plain", string? location = null, TimeSpan? retryAfter = null)
        {
            EnqueueResponse(url, HttpFetchMethod.Head, statusCode, body, contentType, location, retryAfter);
        }

        public int GetRequestCount(string url, HttpFetchMethod method)
        {
            lock (_lock)
            {
                var key = CreateKey(url, method);
                return _requestCounts.TryGetValue(key, out var count) ? count : 0;
            }
        }

        public Task<HttpFetchResult> FetchAsync(Uri url, HttpFetchMethod method, TimeSpan timeout, int maxPageBytes, CancellationToken cancellationToken)
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

            return Task.FromResult(queue.Dequeue().Invoke(maxPageBytes));
        }

        private void EnqueueResponse(string url, HttpFetchMethod method, int statusCode, string body, string contentType, string? location, TimeSpan? retryAfter)
        {
            GetQueue(url, method).Enqueue((maxPageBytes) =>
            {
                var redirectLocation = location is null ? null : new Uri(location, UriKind.RelativeOrAbsolute);

                return Encoding.UTF8.GetByteCount(body) > maxPageBytes
                    ? HttpFetchResult.ResponseTooLarge(statusCode, contentType, redirectLocation, retryAfter)
                    : HttpFetchResult.Response(statusCode, body, contentType, redirectLocation, retryAfter);
            });
        }

        private Queue<Func<int, HttpFetchResult>> GetQueue(string url, HttpFetchMethod method)
        {
            lock (_lock)
            {
                var key = CreateKey(url, method);

                if (!_responses.TryGetValue(key, out var queue))
                {
                    queue = new Queue<Func<int, HttpFetchResult>>();
                    _responses[key] = queue;
                }

                return queue;
            }
        }

        private static string CreateKey(string url, HttpFetchMethod method)
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
