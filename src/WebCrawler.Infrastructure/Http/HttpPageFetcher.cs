using WebCrawler.Application.Ports;
using WebCrawler.Application.Transport;
using System.Net.Http;

namespace WebCrawler.Infrastructure.Http;

public sealed class HttpPageFetcher : IPageFetcher
{
    private readonly HttpClient _httpClient;

    public HttpPageFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SingleFetchResult> FetchAsync(Uri url, FetchRequestMethod method, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(method == FetchRequestMethod.Head ? HttpMethod.Head : HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var body = method == FetchRequestMethod.Head || response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var contentType = response.Content?.Headers.ContentType?.MediaType;
            return SingleFetchResult.Response((int)response.StatusCode, body, contentType, response.Headers.Location);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SingleFetchResult.Timeout();
        }
        catch (HttpRequestException)
        {
            return SingleFetchResult.TransportError();
        }
    }
}
