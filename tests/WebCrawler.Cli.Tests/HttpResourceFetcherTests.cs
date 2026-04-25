using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WebCrawler.Application.Transport;
using WebCrawler.Infrastructure.Http;

namespace WebCrawler.Cli.Tests;

public class HttpPageFetcherTests
{
    [Fact]
    public async Task ReturnsRedirectWithoutReadingOversizedBody()
    {
        var response = CreateResponse(HttpStatusCode.Found, new string('x', 100));
        response.Headers.Location = new Uri("https://example.com/final");
        var fetcher = CreateFetcher(response);

        var result = await fetcher.FetchAsync(new Uri("https://example.com/start"), FetchRequestMethod.Get, TimeSpan.FromSeconds(1), 10, CancellationToken.None);

        Assert.Equal(SingleFetchResultKind.Response, result.Kind);
        Assert.Equal(302, result.StatusCode);
        Assert.Equal("https://example.com/final", result.RedirectLocation?.AbsoluteUri);
        Assert.Equal(string.Empty, result.Body);
    }

    [Fact]
    public async Task ReturnsRetryableStatusWithoutReadingOversizedBody()
    {
        var response = CreateResponse(HttpStatusCode.ServiceUnavailable, new string('x', 100));
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        var fetcher = CreateFetcher(response);

        var result = await fetcher.FetchAsync(new Uri("https://example.com/"), FetchRequestMethod.Get, TimeSpan.FromSeconds(1), 10, CancellationToken.None);

        Assert.Equal(SingleFetchResultKind.Response, result.Kind);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
        Assert.Equal(string.Empty, result.Body);
    }

    [Fact]
    public async Task ReportsOversizedSuccessfulBody()
    {
        var fetcher = CreateFetcher(CreateResponse(HttpStatusCode.OK, new string('x', 100)));

        var result = await fetcher.FetchAsync(new Uri("https://example.com/"), FetchRequestMethod.Get, TimeSpan.FromSeconds(1), 10, CancellationToken.None);

        Assert.Equal(SingleFetchResultKind.ResponseTooLarge, result.Kind);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task DecodesSuccessfulBodyUsingResponseCharset()
    {
        var body = Encoding.Latin1.GetBytes("caf\u00e9");
        var response = CreateResponse(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html")
        {
            CharSet = "iso-8859-1"
        };
        var fetcher = CreateFetcher(response);

        var result = await fetcher.FetchAsync(new Uri("https://example.com/"), FetchRequestMethod.Get, TimeSpan.FromSeconds(1), 100, CancellationToken.None);

        Assert.Equal(SingleFetchResultKind.Response, result.Kind);
        Assert.Equal("caf\u00e9", result.Body);
    }

    private static HttpPageFetcher CreateFetcher(HttpResponseMessage response)
    {
        return new HttpPageFetcher(new HttpClient(new StubHttpMessageHandler(response)));
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body)
    {
        return CreateResponse(statusCode, Encoding.UTF8.GetBytes(body));
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, byte[] body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(body)
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
