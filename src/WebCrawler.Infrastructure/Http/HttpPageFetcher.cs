using WebCrawler.Application.Ports;
using WebCrawler.Application.Transport;
using System.Net.Http;
using System.Text;

namespace WebCrawler.Infrastructure.Http;

public sealed class HttpPageFetcher : IPageFetcher
{
    private readonly HttpClient _httpClient;

    public HttpPageFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SingleFetchResult> FetchAsync(Uri url, FetchRequestMethod method, TimeSpan timeout, int maxPageBytes, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(method == FetchRequestMethod.Head ? HttpMethod.Head : HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var contentType = response.Content?.Headers.ContentType?.MediaType;
            var retryAfter = GetRetryAfter(response);

            if (method == FetchRequestMethod.Head || response.Content is null)
            {
                return SingleFetchResult.Response((int)response.StatusCode, string.Empty, contentType, response.Headers.Location, retryAfter);
            }

            var bodyResult = await ReadBodyAsync(response, maxPageBytes, timeoutCts.Token);

            return bodyResult.IsTooLarge
                ? SingleFetchResult.ResponseTooLarge((int)response.StatusCode, contentType, response.Headers.Location, retryAfter)
                : SingleFetchResult.Response((int)response.StatusCode, bodyResult.Body, contentType, response.Headers.Location, retryAfter);
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

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static async Task<BodyReadResult> ReadBodyAsync(HttpResponseMessage response, int maxPageBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxPageBytes)
        {
            return BodyReadResult.TooLarge();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxPageBytes, 81920));
        var chunk = new byte[81920];
        var totalBytes = 0;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(chunk, cancellationToken);

            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;

            if (totalBytes > maxPageBytes)
            {
                return BodyReadResult.TooLarge();
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return BodyReadResult.Success(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private readonly record struct BodyReadResult(string Body, bool IsTooLarge)
    {
        public static BodyReadResult Success(string body) => new(body, false);

        public static BodyReadResult TooLarge() => new(string.Empty, true);
    }
}
