namespace WebCrawler.Domain.Crawl;

public sealed record Report(Uri SeedUrl, IReadOnlyList<Page> Pages);
