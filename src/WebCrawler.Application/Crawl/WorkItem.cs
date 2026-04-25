namespace WebCrawler.Application.Crawl;

internal sealed record CrawlWorkItem(Uri Url, int Depth, int Attempt)
{
    public CrawlWorkItem NextAttempt() => this with { Attempt = Attempt + 1 };
}
