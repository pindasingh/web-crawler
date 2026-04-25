using WebCrawler.Application.Transport;

namespace WebCrawler.Application.Ports;

public interface IHttpResourceFetcher
{
    Task<HttpFetchResult> FetchAsync(Uri url, HttpFetchMethod method, TimeSpan timeout, int maxPageBytes, CancellationToken cancellationToken);
}
