using Crawler.Domain;
using Crawler.Infrastructure.Persistence;

namespace Crawler.Api;

public sealed record CreateJobRequest(string? Url, int? MaxDepth);

public sealed record CreateJobResponse(Guid JobId);

public sealed record JobResponse(
    Guid Id,
    string Url,
    int MaxDepth,
    int MaxPages,
    JobStatus Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int PagesEnqueued,
    int PagesProcessed,
    bool MaxPagesReached,
    PageCounts? PageCounts)
{
    /// <summary><paramref name="pageCounts"/> is filled in only for a single job, not for history rows.</summary>
    public static JobResponse From(Job job, PageCounts? pageCounts = null) => new(
        job.Id,
        job.Url,
        job.MaxDepth,
        job.MaxPages,
        job.Status,
        job.FailureReason,
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
        job.PagesEnqueued,
        job.PagesProcessed,
        job.PagesEnqueued >= job.MaxPages,
        pageCounts);
}

/// <summary>The job's pages by outcome; Pending pages are still in the frontier.</summary>
public sealed record PageCounts(int Pending, int Fetched, int Skipped, int Failed)
{
    public static PageCounts From(IReadOnlyDictionary<PageStatus, int> byStatus) => new(
        byStatus.GetValueOrDefault(PageStatus.Pending),
        byStatus.GetValueOrDefault(PageStatus.Fetched),
        byStatus.GetValueOrDefault(PageStatus.Skipped),
        byStatus.GetValueOrDefault(PageStatus.Failed));
}

public sealed record JobPageResponse(IReadOnlyList<JobResponse> Items, int Page, int PageSize, int Total);

/// <summary><see cref="Root"/> is null only if the job has no root page, which the API never creates.</summary>
public sealed record JobTreeResponse(Guid JobId, JobStatus Status, TreeNode? Root);

public sealed class JobOptions
{
    public const string Section = "Jobs";

    public int MaxPages { get; set; } = CrawlLimits.DefaultMaxPages;
}
