using WebCrawler.Application.Crawl;

namespace WebCrawler.Application.Ports;

public interface IHtmlDocumentParser
{
    ParsedDocument Parse(string html);
}
