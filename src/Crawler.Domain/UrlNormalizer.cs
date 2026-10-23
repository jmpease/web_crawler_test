namespace Crawler.Domain;

/// <summary>
/// Resolves hrefs against a page URL and reduces them to one canonical form, so the same page
/// reached by different spellings is stored and counted once.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// Resolves <paramref name="href"/> against <paramref name="baseUri"/> and normalizes it.
    /// Returns false for anything that isn't an absolute http(s) URL after resolution
    /// (mailto:, tel:, javascript:, ftp:, malformed input).
    /// </summary>
    public static bool TryNormalize(string? href, Uri baseUri, out Uri? normalized)
    {
        normalized = null;
        if (href is null)
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, href.Trim(), out var resolved) || !IsHttp(resolved))
        {
            return false;
        }

        normalized = Normalize(resolved);
        return true;
    }

    /// <summary>
    /// Lowercases scheme and host, drops the default port and the fragment, and turns an empty
    /// path into "/". Path case, trailing slashes and the query are kept as-is.
    /// </summary>
    public static Uri Normalize(Uri absolute)
    {
        if (!IsHttp(absolute))
        {
            throw new ArgumentException("Only absolute http(s) URLs can be normalized.", nameof(absolute));
        }

        // HttpRequestUrl = scheme, host, non-default port, path and query; no user info, no fragment.
        var text = absolute.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        return new Uri(text, UriKind.Absolute);
    }

    public static bool IsHttp(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
