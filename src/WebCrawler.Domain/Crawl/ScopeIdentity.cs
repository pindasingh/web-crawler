namespace WebCrawler.Domain.Crawl;

public readonly record struct CrawlScopeIdentity(string Host, int? Port);
