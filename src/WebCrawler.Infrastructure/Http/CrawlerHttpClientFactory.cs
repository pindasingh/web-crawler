using System.Net;

namespace WebCrawler.Infrastructure.Http;

public static class CrawlerHttpClientFactory
{
    public static HttpClient CreateDefault()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };

        return new HttpClient(handler, disposeHandler: true);
    }
}
