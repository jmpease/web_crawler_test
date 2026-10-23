using Crawler.Domain;

namespace Crawler.UnitTests;

public class HtmlLinkExtractorTests
{
    private static readonly Uri Page = new("https://example.com/blog/post.html");
    private const string Host = "example.com";

    [Fact]
    public void Page_with_no_links_has_ratio_zero()
    {
        var result = HtmlLinkExtractor.Analyze("<html><body><p>No links</p></body></html>", Page, Host);

        Assert.Equal(0, result.OutgoingLinks);
        Assert.Equal(0, result.SameDomainLinks);
        Assert.Equal(0, result.DomainLinkRatio);
    }

    [Fact]
    public void Mixed_links_give_same_domain_share_of_unique_links()
    {
        const string html = """
            <a href="/a">a</a>
            <a href="https://example.com/b">b</a>
            <a href="c.html">c</a>
            <a href="https://other.org/">other</a>
            """;

        var result = HtmlLinkExtractor.Analyze(html, Page, Host);

        Assert.Equal(4, result.OutgoingLinks);
        Assert.Equal(3, result.SameDomainLinks);
        Assert.Equal(0.75, result.DomainLinkRatio);
    }

    [Fact]
    public void Duplicates_after_normalization_count_once_in_page_order()
    {
        const string html = """
            <a href="/a">1</a>
            <a href="/a#x">2</a>
            <a href="HTTPS://EXAMPLE.COM:443/a">3</a>
            <a href="/b">4</a>
            """;

        var result = HtmlLinkExtractor.Analyze(html, Page, Host);

        Assert.Equal(
            ["https://example.com/a", "https://example.com/b"],
            result.Links.Select(l => l.Url.AbsoluteUri));
    }

    [Fact]
    public void Non_http_links_are_excluded_from_both_counts()
    {
        const string html = """
            <a href="mailto:x@example.com">m</a>
            <a href="tel:123">t</a>
            <a href="javascript:void(0)">j</a>
            <a href="ftp://example.com/f">f</a>
            <a href="/kept">k</a>
            <a>no href</a>
            """;

        var result = HtmlLinkExtractor.Analyze(html, Page, Host);

        Assert.Equal(1, result.OutgoingLinks);
        Assert.Equal(1.0, result.DomainLinkRatio);
    }

    [Fact]
    public void Fragment_only_link_counts_as_an_internal_link_to_the_page_itself()
    {
        var result = HtmlLinkExtractor.Analyze("""<a href="#top">top</a>""", Page, Host);

        var link = Assert.Single(result.Links);
        Assert.Equal(Page, link.Url);
        Assert.True(link.IsSameDomain);
    }

    [Fact]
    public void Www_and_subdomains_are_external()
    {
        const string html = """
            <a href="https://www.example.com/">www</a>
            <a href="https://blog.example.com/">sub</a>
            <a href="https://Example.com/x">same, different case</a>
            """;

        var result = HtmlLinkExtractor.Analyze(html, Page, Host);

        Assert.Equal([false, false, true], result.Links.Select(l => l.IsSameDomain));
    }

    [Fact]
    public void Base_href_changes_how_relative_links_resolve()
    {
        const string html = """
            <head><base href="https://example.com/docs/"></head>
            <a href="page.html">p</a>
            """;

        var result = HtmlLinkExtractor.Analyze(html, Page, Host);

        Assert.Equal("https://example.com/docs/page.html", Assert.Single(result.Links).Url.AbsoluteUri);
    }

    [Fact]
    public void Non_http_base_href_is_ignored()
    {
        const string html = """
            <head><base href="javascript:alert(1)"></head>
            <a href="page.html">p</a>
            """;

        var result = HtmlLinkExtractor.Analyze(html, Page, Host);

        Assert.Equal("https://example.com/blog/page.html", Assert.Single(result.Links).Url.AbsoluteUri);
    }
}
