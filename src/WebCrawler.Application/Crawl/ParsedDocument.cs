namespace WebCrawler.Application.Crawl;

public sealed record ParsedDocument(string? Title, IReadOnlyList<string> Hrefs);
