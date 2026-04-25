using WebCrawler.Application.Ports;
using WebCrawler.Application.Transport;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace WebCrawler.Infrastructure.Http;

public sealed class HttpResourceFetcher : IHttpResourceFetcher
{
    private readonly HttpClient _httpClient;

    public HttpResourceFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<HttpFetchResult> FetchAsync(Uri url, HttpFetchMethod method, TimeSpan timeout, int maxPageBytes, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(method == HttpFetchMethod.Head ? HttpMethod.Head : HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var statusCode = (int)response.StatusCode;
            var contentType = response.Content?.Headers.ContentType?.MediaType;
            var retryAfter = GetRetryAfter(response);

            if (method == HttpFetchMethod.Head || response.Content is null || statusCode < 200 || statusCode >= 300)
            {
                return HttpFetchResult.Response(statusCode, string.Empty, contentType, response.Headers.Location, retryAfter);
            }

            var bodyResult = await ReadBodyAsync(response, maxPageBytes, timeoutCts.Token);

            return bodyResult.IsTooLarge
                ? HttpFetchResult.ResponseTooLarge(statusCode, contentType, response.Headers.Location, retryAfter)
                : HttpFetchResult.Response(statusCode, bodyResult.Body, contentType, response.Headers.Location, retryAfter);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HttpFetchResult.Timeout();
        }
        catch (HttpRequestException)
        {
            return HttpFetchResult.TransportError();
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

        return BodyReadResult.Success(GetEncoding(response.Content.Headers).GetString(buffer.ToArray()));
    }

    private static Encoding GetEncoding(HttpContentHeaders headers)
    {
        var charset = headers.ContentType?.CharSet?.Trim('"');

        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private readonly record struct BodyReadResult(string Body, bool IsTooLarge)
    {
        public static BodyReadResult Success(string body) => new(body, false);

        public static BodyReadResult TooLarge() => new(string.Empty, true);
    }
}
