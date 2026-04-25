namespace WebCrawler.Domain.Crawl;

public sealed record CrawlPage(
    Uri RequestedUrl,
    Uri Url,
    CrawlPageStatus Status,
    int? StatusCode,
    string? Title,
    IReadOnlyList<Uri> Links,
    string? Error,
    IReadOnlyList<Uri> RedirectChain,
    int AttemptCount);
