using System.Threading.Channels;

namespace WebCrawler.Application.Crawl;

internal sealed class Queue
{
    private readonly Channel<CrawlWorkItem> _ready = Channel.CreateUnbounded<CrawlWorkItem>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private readonly PriorityQueue<CrawlWorkItem, DateTimeOffset> _scheduled = new();
    private readonly SemaphoreSlim _changed = new(0);
    private readonly object _lock = new();
    private readonly Task _scheduler;
    private bool _completed;

    public Queue()
    {
        _scheduler = Task.Run(PromoteScheduledWorkAsync);
    }

    public void Enqueue(CrawlWorkItem item)
    {
        if (IsCompleted())
        {
            return;
        }

        _ready.Writer.TryWrite(item);
    }

    public void Enqueue(CrawlWorkItem item, DateTimeOffset readyAtUtc)
    {
        if (readyAtUtc <= DateTimeOffset.UtcNow)
        {
            Enqueue(item);
            return;
        }

        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _scheduled.Enqueue(item, readyAtUtc);
        }

        _changed.Release();
    }

    public IAsyncEnumerable<CrawlWorkItem> ReadAllAsync(CancellationToken cancellationToken)
    {
        return _ready.Reader.ReadAllAsync(cancellationToken);
    }

    public void Complete()
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _scheduled.Clear();
        }

        _ready.Writer.TryComplete();
        _changed.Release();
    }

    private async Task PromoteScheduledWorkAsync()
    {
        while (true)
        {
            var readyItems = new List<CrawlWorkItem>();
            DateTimeOffset? nextReadyAtUtc = null;

            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;

                while (_scheduled.TryPeek(out _, out var readyAtUtc) && readyAtUtc <= now)
                {
                    readyItems.Add(_scheduled.Dequeue());
                }

                if (readyItems.Count == 0 && _scheduled.TryPeek(out _, out var nextReadyAt))
                {
                    nextReadyAtUtc = nextReadyAt;
                }
            }

            foreach (var item in readyItems)
            {
                _ready.Writer.TryWrite(item);
            }

            if (readyItems.Count > 0)
            {
                continue;
            }

            if (nextReadyAtUtc is null)
            {
                await _changed.WaitAsync();
                continue;
            }

            var delay = nextReadyAtUtc.Value - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero)
            {
                await _changed.WaitAsync(delay);
            }
        }
    }

    private bool IsCompleted()
    {
        lock (_lock)
        {
            return _completed;
        }
    }
}
