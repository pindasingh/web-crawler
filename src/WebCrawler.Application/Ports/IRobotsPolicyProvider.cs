using WebCrawler.Domain.Robots;

namespace WebCrawler.Application.Ports;

public interface IRobotsPolicyProvider
{
    Task<RobotsRules> GetRulesAsync(Uri seedUrl, CancellationToken cancellationToken);
}
