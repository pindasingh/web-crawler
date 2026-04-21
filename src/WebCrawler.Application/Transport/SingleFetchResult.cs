namespace WebCrawler.Application.Transport;

public sealed record SingleFetchResult(
    SingleFetchResultKind Kind,
    int? StatusCode,
    string Body,
    string? ContentType,
    Uri? RedirectLocation)
{
    public static SingleFetchResult Response(int statusCode, string body, string? contentType, Uri? redirectLocation)
    {
        return new SingleFetchResult(SingleFetchResultKind.Response, statusCode, body, contentType, redirectLocation);
    }

    public static SingleFetchResult Timeout()
    {
        return new SingleFetchResult(SingleFetchResultKind.Timeout, null, string.Empty, null, null);
    }

    public static SingleFetchResult TransportError()
    {
        return new SingleFetchResult(SingleFetchResultKind.TransportError, null, string.Empty, null, null);
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
    Timeout,
    TransportError
}
