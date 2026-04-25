namespace WebCrawler.Application.Crawl;

public sealed record Options
{
    public const int DefaultMaxPageBytes = 2 * 1024 * 1024;

    public int? WorkerCount { get; init; }

    public int? MaxDepth { get; init; }

    public int MaxPageBytes { get; init; } = DefaultMaxPageBytes;

    public int MaxAttempts { get; init; } = 3;

    public int MaxRedirects { get; init; } = 10;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public int EffectiveWorkerCount()
    {
        return WorkerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 8);
    }
}
