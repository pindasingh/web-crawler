namespace WebCrawler.Application.Transport;

public sealed record HttpFetchResult(
    HttpFetchResultKind Kind,
    int? StatusCode,
    string Body,
    string? ContentType,
    Uri? RedirectLocation,
    TimeSpan? RetryAfter)
{
    public static HttpFetchResult Response(int statusCode, string body, string? contentType, Uri? redirectLocation, TimeSpan? retryAfter = null)
    {
        return new HttpFetchResult(HttpFetchResultKind.Response, statusCode, body, contentType, redirectLocation, retryAfter);
    }

    public static HttpFetchResult ResponseTooLarge(int statusCode, string? contentType, Uri? redirectLocation, TimeSpan? retryAfter = null)
    {
        return new HttpFetchResult(HttpFetchResultKind.ResponseTooLarge, statusCode, string.Empty, contentType, redirectLocation, retryAfter);
    }

    public static HttpFetchResult Timeout()
    {
        return new HttpFetchResult(HttpFetchResultKind.Timeout, null, string.Empty, null, null, null);
    }

    public static HttpFetchResult TransportError()
    {
        return new HttpFetchResult(HttpFetchResultKind.TransportError, null, string.Empty, null, null, null);
    }
}

public enum HttpFetchMethod
{
    Head,
    Get
}

public enum HttpFetchResultKind
{
    Response,
    ResponseTooLarge,
    Timeout,
    TransportError
}
