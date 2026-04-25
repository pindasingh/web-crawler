using WebCrawler.Application.Ports;
using WebCrawler.Domain.Crawl;
using WebCrawler.Domain.Robots;

namespace WebCrawler.Infrastructure.Robots;

public sealed class HttpRobotsPolicyProvider : IRobotsPolicyProvider
{
    private readonly HttpClient _httpClient;

    public HttpRobotsPolicyProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<RobotsRules> GetRulesAsync(Uri seedUrl, CancellationToken cancellationToken)
    {
        var robotsUri = new Uri(seedUrl, "/robots.txt");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await GetWithScopedRedirectsAsync(robotsUri, seedUrl, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return RobotsRules.AllowAll();
            }

            var content = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return RobotsRules.Parse(content);
        }
        catch
        {
            return RobotsRules.AllowAll();
        }
    }

    private async Task<HttpResponseMessage> GetWithScopedRedirectsAsync(Uri robotsUri, Uri seedUrl, CancellationToken cancellationToken)
    {
        var currentUri = robotsUri;

        for (var redirectCount = 0; redirectCount <= 10; redirectCount++)
        {
            var response = await _httpClient.GetAsync(currentUri, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (!CrawlUrlRules.IsRedirectStatus(statusCode))
            {
                return response;
            }

            if (response.Headers.Location is null)
            {
                response.Dispose();
                throw new InvalidOperationException("Invalid robots redirect.");
            }

            var nextUri = CrawlUrlRules.ResolveRedirect(currentUri, response.Headers.Location);
            response.Dispose();

            if (!CrawlUrlRules.IsInScope(nextUri, seedUrl))
            {
                throw new InvalidOperationException("Robots redirect left crawl scope.");
            }

            currentUri = nextUri;
        }

        throw new InvalidOperationException("Robots redirect limit exceeded.");
    }
}
