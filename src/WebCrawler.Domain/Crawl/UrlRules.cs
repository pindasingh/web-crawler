namespace WebCrawler.Domain.Crawl;

public static class UrlRules
{
    public static Uri ParseSeed(string seedInput)
    {
        if (!Uri.TryCreate(seedInput, UriKind.Absolute, out var uri) || !IsSupportedScheme(uri))
        {
            throw new ArgumentException("Seed must be an absolute http or https URL.", nameof(seedInput));
        }

        return StripFragment(uri);
    }

    public static bool TryResolveLink(Uri baseUrl, string href, out Uri resolvedLink)
    {
        resolvedLink = baseUrl;

        if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#'))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl, href, out var candidate))
        {
            return false;
        }

        resolvedLink = StripFragment(candidate);
        return true;
    }

    public static Uri ResolveRedirect(Uri currentUrl, Uri location)
    {
        return location.IsAbsoluteUri
            ? StripFragment(location)
            : StripFragment(new Uri(currentUrl, location));
    }

    public static bool IsInScope(Uri url, Uri seedUrl)
    {
        return IsSupportedScheme(url)
            && HasSameScopeIdentity(url, seedUrl);
    }

    public static bool HasSameScopeIdentity(Uri left, Uri right)
    {
        return CreateScopeIdentity(left) == CreateScopeIdentity(right);
    }

    public static ScopeIdentity CreateScopeIdentity(Uri uri)
    {
        var normalizedHost = uri.IdnHost.ToLowerInvariant();
        var normalizedPort = NormalizeScopePort(uri);
        return new ScopeIdentity(normalizedHost, normalizedPort);
    }

    public static bool RequiresScopeProbe(Uri candidate, Uri seedUrl)
    {
        return candidate.Host.EndsWith(".", StringComparison.Ordinal)
            || !HasSameScopeIdentity(candidate, seedUrl);
    }

    public static bool IsSupportedScheme(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    public static Uri StripFragment(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    public static bool IsRedirectStatus(int statusCode)
    {
        return statusCode is 301 or 302 or 303 or 307 or 308;
    }

    public static bool IsTransientStatus(int statusCode)
    {
        return statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    }

    private static int? NormalizeScopePort(Uri uri)
    {
        if (uri.IsDefaultPort)
        {
            return null;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.Port == 80)
        {
            return null;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && uri.Port == 443)
        {
            return null;
        }

        return uri.Port;
    }
}
