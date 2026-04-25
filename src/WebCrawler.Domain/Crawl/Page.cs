namespace WebCrawler.Domain.Crawl;

public sealed record Page(
    Uri RequestedUrl,
    Uri Url,
    PageStatus Status,
    int? StatusCode,
    string? Title,
    IReadOnlyList<Uri> Links,
    string? Error,
    IReadOnlyList<Uri> RedirectChain,
    int AttemptCount);
