using WebCrawler.Application.Transport;

namespace WebCrawler.Application.Ports;

public interface IPageFetcher
{
    Task<SingleFetchResult> FetchAsync(Uri url, FetchRequestMethod method, TimeSpan timeout, int maxPageBytes, CancellationToken cancellationToken);
}
