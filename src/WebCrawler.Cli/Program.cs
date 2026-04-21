using WebCrawler.Application.Crawl;
using WebCrawler.Cli;
using WebCrawler.Infrastructure.Html;
using WebCrawler.Infrastructure.Http;
using WebCrawler.Infrastructure.Robots;

using var httpClient = CrawlerHttpClientFactory.CreateDefault();
var crawlSiteService = new CrawlSiteService(
    new HttpPageFetcher(httpClient),
    new HttpRobotsPolicyProvider(httpClient),
    new HtmlAgilityDocumentParser());
var exitCode = await CliApplication.RunAsync(args, Console.Out, crawlSiteService, CancellationToken.None);
Environment.ExitCode = exitCode;
