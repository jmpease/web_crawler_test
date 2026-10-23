using Crawler.Domain;

namespace Crawler.Infrastructure.Persistence;

public sealed class Job
{
    public Guid Id { get; set; }
    public required string Url { get; set; }
    public required string StartHost { get; set; }
    public int MaxDepth { get; set; }
    public int MaxPages { get; set; }
    public JobStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Pages inserted for this job, including the root; capped by <see cref="MaxPages"/>.</summary>
    public int PagesEnqueued { get; set; }

    /// <summary>Pages that reached a terminal status (Fetched, Skipped or Failed).</summary>
    public int PagesProcessed { get; set; }
}

public sealed class Page
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public required string Url { get; set; }

    /// <summary>SHA-256 of the normalized URL; the dedupe key within a job.</summary>
    public required byte[] UrlHash { get; set; }

    public int Depth { get; set; }

    /// <summary>The page that first discovered this one (BFS tree parent); null for the root.</summary>
    public long? ParentPageId { get; set; }

    public PageStatus Status { get; set; }
    public string? FinalUrl { get; set; }
    public int? HttpStatus { get; set; }
    public string? ContentType { get; set; }
    public int? OutgoingLinks { get; set; }
    public int? SameDomainLinks { get; set; }
    public double? DomainLinkRatio { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
}

/// <summary>One outgoing link from a fetched page, internal or external; together they form the full link graph.</summary>
public sealed class Edge
{
    public Guid JobId { get; set; }
    public long FromPageId { get; set; }
    public required string ToUrl { get; set; }
    public required byte[] ToUrlHash { get; set; }
    public bool IsSameDomain { get; set; }
}
