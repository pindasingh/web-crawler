namespace WebCrawler.Domain.Crawl;

public sealed record CrawlReport(Uri SeedUrl, IReadOnlyList<CrawlPage> Pages);
