using AngleSharp.Html.Parser;

namespace Crawler.Domain;

/// <summary>Pulls anchor hrefs out of an HTML document and analyzes them. Pure: parses a string, no I/O.</summary>
public static class HtmlLinkExtractor
{
    private static readonly HtmlParser Parser = new();

    public static LinkAnalysis Analyze(string html, Uri pageUrl, string startHost)
    {
        using var document = Parser.ParseDocument(html);

        var baseUri = ResolveBase(document.QuerySelector("base[href]")?.GetAttribute("href"), pageUrl);
        var hrefs = document.QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href"))
            .OfType<string>();

        return LinkAnalysis.From(hrefs, baseUri, startHost);
    }

    // A <base href> changes how relative links resolve; ignore it unless it is http(s).
    private static Uri ResolveBase(string? baseHref, Uri pageUrl) =>
        baseHref is not null
        && Uri.TryCreate(pageUrl, baseHref.Trim(), out var resolved)
        && UrlNormalizer.IsHttp(resolved)
            ? resolved
            : pageUrl;
}
