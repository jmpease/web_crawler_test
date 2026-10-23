namespace Crawler.Domain;

public sealed record DiscoveredLink(Uri Url, bool IsSameDomain);

/// <summary>
/// The unique, normalized http(s) links found on one page, split by whether they stay on the
/// crawl's starting host.
/// </summary>
public sealed record LinkAnalysis(IReadOnlyList<DiscoveredLink> Links)
{
    public int OutgoingLinks => Links.Count;

    public int SameDomainLinks => Links.Count(l => l.IsSameDomain);

    /// <summary>Same-domain links divided by all links; 0 when the page has no links.</summary>
    public double DomainLinkRatio => OutgoingLinks == 0 ? 0 : (double)SameDomainLinks / OutgoingLinks;

    /// <summary>
    /// Normalizes each href against <paramref name="baseUri"/>, drops non-http(s) ones and
    /// duplicates (first occurrence wins, preserving page order), and classifies the rest.
    /// </summary>
    public static LinkAnalysis From(IEnumerable<string> hrefs, Uri baseUri, string startHost)
    {
        var seen = new HashSet<Uri>();
        var links = new List<DiscoveredLink>();
        foreach (var href in hrefs)
        {
            if (UrlNormalizer.TryNormalize(href, baseUri, out var url) && seen.Add(url!))
            {
                links.Add(new DiscoveredLink(url!, IsSameHost(url!, startHost)));
            }
        }

        return new LinkAnalysis(links);
    }

    /// <summary>Exact host match, case-insensitive; "www." and subdomains count as external.</summary>
    public static bool IsSameHost(Uri url, string startHost) =>
        string.Equals(url.Host, startHost, StringComparison.OrdinalIgnoreCase);
}
