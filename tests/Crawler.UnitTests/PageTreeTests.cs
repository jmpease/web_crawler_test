using Crawler.Domain;

namespace Crawler.UnitTests;

public class PageTreeTests
{
    [Fact]
    public void Returns_null_when_there_is_no_root_page()
    {
        Assert.Null(PageTree.Build([], []));
    }

    [Fact]
    public void Nests_pages_under_their_parent_in_discovery_order()
    {
        TreePage[] pages =
        [
            Page(1, null, "https://a.test/", 0),
            Page(3, 1, "https://a.test/b", 1),
            Page(2, 1, "https://a.test/a", 1),
            Page(4, 2, "https://a.test/a/deep", 2),
        ];

        var root = PageTree.Build(pages, [])!;

        Assert.Equal("https://a.test/", root.Url);
        Assert.Equal(["https://a.test/a", "https://a.test/b"], root.Children.Select(c => c.Url));
        var deep = Assert.Single(root.Children[0].Children);
        Assert.Equal(2, deep.Depth);
        Assert.Empty(root.Children[1].Children);
    }

    [Fact]
    public void Attaches_each_page_its_own_links_sorted_by_url()
    {
        TreePage[] pages = [Page(1, null, "https://a.test/", 0), Page(2, 1, "https://a.test/x", 1)];
        TreeLink[] links =
        [
            new(1, "https://z.test/", false),
            new(1, "https://a.test/x", true),
            new(2, "https://a.test/", true),
        ];

        var root = PageTree.Build(pages, links)!;

        Assert.Equal(
            [new OutgoingLink("https://a.test/x", true), new OutgoingLink("https://z.test/", false)],
            root.OutgoingLinks);
        Assert.Equal([new OutgoingLink("https://a.test/", true)], root.Children[0].OutgoingLinks);
    }

    [Fact]
    public void Carries_status_ratio_and_error()
    {
        TreePage[] pages =
        [
            Page(1, null, "https://a.test/", 0) with { Status = PageStatus.Fetched, DomainLinkRatio = 0.5 },
            Page(2, 1, "https://a.test/broken", 1) with { Status = PageStatus.Failed, Error = "HTTP 500" },
        ];

        var root = PageTree.Build(pages, [])!;

        Assert.Equal(PageStatus.Fetched, root.Status);
        Assert.Equal(0.5, root.DomainLinkRatio);
        Assert.Equal(PageStatus.Failed, root.Children[0].Status);
        Assert.Null(root.Children[0].DomainLinkRatio);
        Assert.Equal("HTTP 500", root.Children[0].Error);
    }

    private static TreePage Page(long id, long? parentId, string url, int depth) =>
        new(id, parentId, url, depth, PageStatus.Pending, null, null);
}
