namespace WebCrawler.Application.Transport;

public sealed record SingleFetchResult(
    SingleFetchResultKind Kind,
    int? StatusCode,
    string Body,
    string? ContentType,
    Uri? RedirectLocation,
    TimeSpan? RetryAfter)
{
    public static SingleFetchResult Response(int statusCode, string body, string? contentType, Uri? redirectLocation, TimeSpan? retryAfter = null)
    {
        return new SingleFetchResult(SingleFetchResultKind.Response, statusCode, body, contentType, redirectLocation, retryAfter);
    }

    public static SingleFetchResult ResponseTooLarge(int statusCode, string? contentType, Uri? redirectLocation, TimeSpan? retryAfter = null)
    {
        return new SingleFetchResult(SingleFetchResultKind.ResponseTooLarge, statusCode, string.Empty, contentType, redirectLocation, retryAfter);
    }

    public static SingleFetchResult Timeout()
    {
        return new SingleFetchResult(SingleFetchResultKind.Timeout, null, string.Empty, null, null, null);
    }

    public static SingleFetchResult TransportError()
    {
        return new SingleFetchResult(SingleFetchResultKind.TransportError, null, string.Empty, null, null, null);
    }
}

public enum FetchRequestMethod
{
    Head,
    Get
}

public enum SingleFetchResultKind
{
    Response,
    ResponseTooLarge,
    Timeout,
    TransportError
}
