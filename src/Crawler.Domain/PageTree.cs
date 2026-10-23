namespace Crawler.Domain;

/// <summary>A stored page as the tree needs it; <see cref="ParentId"/> is the page that first discovered it.</summary>
public sealed record TreePage(long Id, long? ParentId, string Url, int Depth, PageStatus Status, double? DomainLinkRatio, string? Error);

/// <summary>One stored outgoing link of a page.</summary>
public sealed record TreeLink(long FromPageId, string Url, bool IsSameDomain);

public sealed record OutgoingLink(string Url, bool IsSameDomain);

public sealed record TreeNode(
    string Url,
    int Depth,
    PageStatus Status,
    double? DomainLinkRatio,
    string? Error,
    IReadOnlyList<OutgoingLink> OutgoingLinks,
    IReadOnlyList<TreeNode> Children);

/// <summary>
/// Builds the crawl tree in memory from a job's flat pages and links, so the API reads each table once.
/// </summary>
public static class PageTree
{
    /// <summary>
    /// Nests pages under their first-discovery parent. Children keep discovery order (page id);
    /// outgoing links are sorted by URL. Returns null when the job has no root page.
    /// </summary>
    public static TreeNode? Build(IReadOnlyCollection<TreePage> pages, IEnumerable<TreeLink> links)
    {
        var root = pages.Where(p => p.ParentId is null).MinBy(p => p.Id);
        if (root is null)
        {
            return null;
        }

        var childrenByParent = pages.Where(p => p.ParentId is not null).ToLookup(p => p.ParentId!.Value);
        var linksByPage = links.ToLookup(l => l.FromPageId);
        return BuildNode(root, childrenByParent, linksByPage);
    }

    // Recursion depth is bounded by the job's max depth, and a parent always exists before its child, so there are no cycles.
    private static TreeNode BuildNode(TreePage page, ILookup<long, TreePage> childrenByParent, ILookup<long, TreeLink> linksByPage) =>
        new(
            page.Url,
            page.Depth,
            page.Status,
            page.DomainLinkRatio,
            page.Error,
            linksByPage[page.Id]
                .OrderBy(l => l.Url, StringComparer.Ordinal)
                .Select(l => new OutgoingLink(l.Url, l.IsSameDomain))
                .ToList(),
            childrenByParent[page.Id]
                .OrderBy(c => c.Id)
                .Select(c => BuildNode(c, childrenByParent, linksByPage))
                .ToList());
}
