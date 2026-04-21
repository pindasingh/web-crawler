using WebCrawler.Application.Ports;
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

            using var response = await _httpClient.GetAsync(robotsUri, timeoutCts.Token);

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
}
