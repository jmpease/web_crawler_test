using Crawler.Domain;

namespace Crawler.UnitTests;

public class UrlNormalizerTests
{
    private static readonly Uri Page = new("https://example.com/docs/guide/intro.html?v=1");

    [Theory]
    [InlineData("../api/index.html", "https://example.com/docs/api/index.html")]
    [InlineData("/about", "https://example.com/about")]
    [InlineData("?q=2", "https://example.com/docs/guide/intro.html?q=2")]
    [InlineData("next.html", "https://example.com/docs/guide/next.html")]
    [InlineData("//cdn.example.org/x", "https://cdn.example.org/x")]
    [InlineData("  /padded  ", "https://example.com/padded")]
    public void Resolves_relative_hrefs_against_the_page(string href, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(href, Page, out var url));
        Assert.Equal(expected, url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("/a#section", "https://example.com/a")]
    [InlineData("#top", "https://example.com/docs/guide/intro.html?v=1")]
    [InlineData("", "https://example.com/docs/guide/intro.html?v=1")]
    public void Strips_fragments_so_fragment_only_links_point_at_the_page_itself(string href, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(href, Page, out var url));
        Assert.Equal(expected, url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("HTTPS://Example.COM:443", "https://example.com/")]
    [InlineData("http://example.com:80/Path/", "http://example.com/Path/")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")]
    [InlineData("https://user:pw@example.com/a", "https://example.com/a")]
    public void Lowercases_host_drops_default_port_and_keeps_path_case(string href, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(href, Page, out var url));
        Assert.Equal(expected, url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+15551234567")]
    [InlineData("javascript:void(0)")]
    [InlineData("ftp://example.com/file.txt")]
    [InlineData("data:text/html,hi")]
    [InlineData(null)]
    public void Rejects_non_http_schemes(string? href)
    {
        Assert.False(UrlNormalizer.TryNormalize(href, Page, out var url));
        Assert.Null(url);
    }
}
