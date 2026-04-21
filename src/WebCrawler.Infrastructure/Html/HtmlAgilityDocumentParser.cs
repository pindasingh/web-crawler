using HtmlAgilityPack;
using WebCrawler.Application.Crawl;
using WebCrawler.Application.Ports;

namespace WebCrawler.Infrastructure.Html;

public sealed class HtmlAgilityDocumentParser : IHtmlDocumentParser
{
    public ParsedDocument Parse(string html)
    {
        var document = new HtmlDocument();
        document.OptionFixNestedTags = true;
        document.LoadHtml(html);

        var anchors = document.DocumentNode.SelectNodes("//a[@href]");
        var hrefs = new List<string>(anchors?.Count ?? 0);

        if (anchors is not null)
        {
            foreach (var anchor in anchors)
            {
                var href = anchor.GetAttributeValue("href", string.Empty)?.Trim();

                if (!string.IsNullOrWhiteSpace(href))
                {
                    hrefs.Add(href);
                }
            }
        }

        var title = document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? null : HtmlEntity.DeEntitize(title);
        return new ParsedDocument(normalizedTitle, hrefs);
    }
}
