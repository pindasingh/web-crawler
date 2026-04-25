using System.Globalization;
using System.Text.Json;
using WebCrawler.Application.Crawl;
using WebCrawler.Domain.Crawl;

namespace WebCrawler.Cli;

public static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, CrawlSiteService crawlSiteService, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args.Any(static arg => arg is "--help" or "-h"))
        {
            await WriteUsageAsync(output, cancellationToken);
            return args.Length == 0 ? 1 : 0;
        }

        if (!TryParseArgs(args, out var seedInput, out var options, out var error))
        {
            await output.WriteLineAsync(error.AsMemory(), cancellationToken);
            await output.WriteLineAsync();
            await WriteUsageAsync(output, cancellationToken);
            return 1;
        }

        CrawlReport report;

        try
        {
            report = await crawlSiteService.CrawlAsync(seedInput, options, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            await output.WriteLineAsync(exception.Message.AsMemory(), cancellationToken);
            return 1;
        }

        foreach (var page in report.Pages)
        {
            var line = JsonSerializer.Serialize(ToOutputRecord(page), JsonOptions);
            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
        }

        return 0;
    }

    private static bool TryParseArgs(string[] args, out string seedInput, out CrawlerOptions options, out string error)
    {
        seedInput = string.Empty;
        error = string.Empty;
        options = new CrawlerOptions();
        string? seed = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                if (seed is not null)
                {
                    error = "Only a single seed URL may be provided.";
                    return false;
                }

                seed = arg;
                continue;
            }

            switch (arg)
            {
                case "--workers":
                    if (!TryReadPositiveInt(args, ref index, out var workers, out error))
                    {
                        return false;
                    }

                    options = options with { WorkerCount = workers };
                    break;

                case "--timeout-ms":
                    if (!TryReadPositiveInt(args, ref index, out var timeoutMs, out error))
                    {
                        return false;
                    }

                    options = options with { RequestTimeout = TimeSpan.FromMilliseconds(timeoutMs) };
                    break;

                case "--retry-delay-ms":
                    if (!TryReadPositiveInt(args, ref index, out var retryDelayMs, out error))
                    {
                        return false;
                    }

                    options = options with { BaseRetryDelay = TimeSpan.FromMilliseconds(retryDelayMs) };
                    break;

                case "--max-redirects":
                    if (!TryReadNonNegativeInt(args, ref index, out var maxRedirects, out error))
                    {
                        return false;
                    }

                    options = options with { MaxRedirects = maxRedirects };
                    break;

                case "--max-depth":
                    if (!TryReadNonNegativeInt(args, ref index, out var maxDepth, out error))
                    {
                        return false;
                    }

                    options = options with { MaxDepth = maxDepth };
                    break;

                case "--max-page-bytes":
                    if (!TryReadPositiveInt(args, ref index, out var maxPageBytes, out error))
                    {
                        return false;
                    }

                    options = options with { MaxPageBytes = maxPageBytes };
                    break;

                default:
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(seed))
        {
            error = "A seed URL is required.";
            return false;
        }

        seedInput = seed;
        return true;
    }

    private static async Task WriteUsageAsync(TextWriter output, CancellationToken cancellationToken)
    {
        const string usage = "Usage: webcrawler <seed-url> [--workers N] [--timeout-ms N] [--retry-delay-ms N] [--max-redirects N] [--max-depth N] [--max-page-bytes N]";
        await output.WriteLineAsync(usage.AsMemory(), cancellationToken);
    }

    private static CrawlOutputRecord ToOutputRecord(CrawlPage page)
    {
        return new CrawlOutputRecord(
            page.Url.AbsoluteUri,
            page.Status switch
            {
                CrawlPageStatus.Succeeded => "succeeded",
                CrawlPageStatus.RedirectedOutOfScope => "redirected-out-of-scope",
                _ => "failed"
            },
            page.Title,
            page.Links.Select(static link => link.AbsoluteUri).ToArray(),
            page.StatusCode,
            page.Error,
            page.RedirectChain.Select(static uri => uri.AbsoluteUri).ToArray(),
            page.AttemptCount);
    }

    private static bool TryReadPositiveInt(string[] args, ref int index, out int value, out string error)
    {
        if (!TryReadInt(args, ref index, out value, out error))
        {
            return false;
        }

        if (value <= 0)
        {
            error = "Option value must be greater than zero.";
            return false;
        }

        return true;
    }

    private static bool TryReadNonNegativeInt(string[] args, ref int index, out int value, out string error)
    {
        if (!TryReadInt(args, ref index, out value, out error))
        {
            return false;
        }

        if (value < 0)
        {
            error = "Option value must be zero or greater.";
            return false;
        }

        return true;
    }

    private static bool TryReadInt(string[] args, ref int index, out int value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (index + 1 >= args.Length)
        {
            error = $"Missing value for option {args[index]}.";
            return false;
        }

        index++;

        if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"Invalid integer value: {args[index]}";
            return false;
        }

        return true;
    }

    private sealed record CrawlOutputRecord(
        string Url,
        string Status,
        string? Title,
        IReadOnlyList<string> Links,
        int? StatusCode,
        string? Error,
        IReadOnlyList<string> RedirectChain,
        int AttemptCount);
}
