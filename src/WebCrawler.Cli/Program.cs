using WebCrawler.Application.Crawl;
using WebCrawler.Cli;
using WebCrawler.Infrastructure.Html;
using WebCrawler.Infrastructure.Http;
using WebCrawler.Infrastructure.Robots;

using var httpClient = HttpClientFactory.CreateDefault();
var siteService = new SiteService(
    new HttpResourceFetcher(httpClient),
    new HttpRobotsPolicyProvider(httpClient),
    new HtmlAgilityDocumentParser());
var exitCode = await CliApplication.RunAsync(args, Console.Out, siteService, CancellationToken.None);
Environment.ExitCode = exitCode;
