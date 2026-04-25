namespace WebCrawler.Application.Crawl;

internal sealed record WorkItem(Uri Url, int Depth, int Attempt)
{
    public WorkItem NextAttempt() => this with { Attempt = Attempt + 1 };
}
