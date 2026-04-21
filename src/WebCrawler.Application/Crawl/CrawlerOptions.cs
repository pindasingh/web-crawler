namespace WebCrawler.Application.Crawl;

public sealed record CrawlerOptions
{
    public int? WorkerCount { get; init; }

    public int MaxAttempts { get; init; } = 3;

    public int MaxRedirects { get; init; } = 10;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    public int EffectiveWorkerCount()
    {
        return WorkerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 8);
    }
}
