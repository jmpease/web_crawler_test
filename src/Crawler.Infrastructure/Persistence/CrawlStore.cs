using Crawler.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Infrastructure.Persistence;

public sealed record CrawlJobState(Guid Id, string StartHost, int MaxDepth);

public sealed record PendingPage(long Id, string Url, int Depth, long? ParentPageId);

/// <summary>What the worker learned about one page; <see cref="Links"/> is set only for a Fetched page.</summary>
public sealed record PageOutcome(PageStatus Status, string? FinalUrl, int? HttpStatus, string? ContentType, string? Error, LinkAnalysis? Links);

public sealed record RootPageState(PageStatus Status, string? Error);

/// <summary>
/// The worker's reads and writes. The BFS frontier is the job's Pending pages, so a redelivered message resumes where
/// the last attempt stopped (D-13). Every write is conditional, so a duplicate delivery can't double-count.
/// </summary>
public sealed class CrawlStore(CrawlerDbContext db)
{
    /// <summary>
    /// Pending → Running (setting started_at once); Running stays Running so a redelivery resumes.
    /// Returns null when the job is missing or already terminal, i.e. a duplicate delivery.
    /// </summary>
    public async Task<CrawlJobState?> StartAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var started = await db.Jobs
            .Where(j => j.Id == jobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now), cancellationToken);
        if (started == 0)
        {
            return null;
        }

        return await db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new CrawlJobState(j.Id, j.StartHost, j.MaxDepth))
            .SingleAsync(cancellationToken);
    }

    /// <summary>The next page to crawl, shallowest first, then in discovery order.</summary>
    public Task<PendingPage?> NextPendingPageAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Pages.AsNoTracking()
            .Where(p => p.JobId == jobId && p.Status == PageStatus.Pending)
            .OrderBy(p => p.Depth)
            .ThenBy(p => p.Id)
            .Select(p => new PendingPage(p.Id, p.Url, p.Depth, p.ParentPageId))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Records the page's outcome, its outgoing links and its newly discovered same-host children in one transaction,
    /// with the job row locked so the max-pages cap and counters stay exact. Returns false, writing nothing, when the
    /// page was no longer Pending (a concurrent duplicate delivery got there first).
    /// </summary>
    public async Task<bool> SavePageAsync(CrawlJobState job, PendingPage page, PageOutcome outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var remaining = (await db.Database
            .SqlQuery<int>($"SELECT max_pages - pages_enqueued AS \"Value\" FROM jobs WHERE id = {job.Id} FOR UPDATE")
            .ToListAsync(cancellationToken)).Single();

        if (!await MarkPageAsync(page, outcome, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var enqueued = 0;
        if (outcome.Links is { } links)
        {
            await InsertEdgesAsync(job, page, links, cancellationToken);
            if (page.Depth < job.MaxDepth && remaining > 0)
            {
                enqueued = await EnqueueChildrenAsync(job, page, links, remaining, cancellationToken);
            }
        }

        await db.Jobs
            .Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.PagesEnqueued, j => j.PagesEnqueued + enqueued)
                .SetProperty(j => j.PagesProcessed, j => j.PagesProcessed + 1), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<RootPageState> GetRootPageAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Pages.AsNoTracking()
            .Where(p => p.JobId == jobId && p.ParentPageId == null)
            .Select(p => new RootPageState(p.Status, p.Error))
            .SingleAsync(cancellationToken);

    /// <summary>Moves a Running job to Completed or Failed; a job already finished elsewhere is left alone.</summary>
    public Task FinishAsync(Guid jobId, JobStatus status, string? failureReason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.FailureReason, failureReason)
                .SetProperty(j => j.CompletedAt, now), cancellationToken);
    }

    private async Task<bool> MarkPageAsync(PendingPage page, PageOutcome outcome, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var outgoing = outcome.Links?.OutgoingLinks;
        var sameDomain = outcome.Links?.SameDomainLinks;
        var ratio = outcome.Links?.DomainLinkRatio;
        var updated = await db.Pages
            .Where(p => p.Id == page.Id && p.Status == PageStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, outcome.Status)
                .SetProperty(p => p.FinalUrl, outcome.FinalUrl)
                .SetProperty(p => p.HttpStatus, outcome.HttpStatus)
                .SetProperty(p => p.ContentType, outcome.ContentType)
                .SetProperty(p => p.Error, outcome.Error)
                .SetProperty(p => p.OutgoingLinks, outgoing)
                .SetProperty(p => p.SameDomainLinks, sameDomain)
                .SetProperty(p => p.DomainLinkRatio, ratio)
                .SetProperty(p => p.FetchedAt, now), cancellationToken);
        return updated == 1;
    }

    private Task<int> InsertEdgesAsync(CrawlJobState job, PendingPage page, LinkAnalysis links, CancellationToken cancellationToken)
    {
        if (links.OutgoingLinks == 0)
        {
            return Task.FromResult(0);
        }

        var urls = links.Links.Select(l => l.Url.AbsoluteUri).ToArray();
        var hashes = urls.Select(UrlHash.Compute).ToArray();
        var sameDomain = links.Links.Select(l => l.IsSameDomain).ToArray();
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO edges (job_id, from_page_id, to_url, to_url_hash, is_same_domain)
            SELECT {job.Id}, {page.Id}, l.url, l.hash, l.same
            FROM unnest({urls}, {hashes}, {sameDomain}) AS l(url, hash, same)
            ON CONFLICT DO NOTHING
            """, cancellationToken);
    }

    /// <summary>
    /// Inserts same-host links not yet known for this job as Pending children, in page order, at most
    /// <paramref name="remaining"/> of them. The job row lock makes the NOT EXISTS check exact; ON CONFLICT is a backstop.
    /// </summary>
    private async Task<int> EnqueueChildrenAsync(
        CrawlJobState job, PendingPage page, LinkAnalysis links, int remaining, CancellationToken cancellationToken)
    {
        var urls = links.Links.Where(l => l.IsSameDomain).Select(l => l.Url.AbsoluteUri).ToArray();
        if (urls.Length == 0)
        {
            return 0;
        }

        var hashes = urls.Select(UrlHash.Compute).ToArray();
        var depth = page.Depth + 1;
        var pending = nameof(PageStatus.Pending);
        return await db.Database.ExecuteSqlAsync($"""
            INSERT INTO pages (job_id, url, url_hash, depth, parent_page_id, status)
            SELECT {job.Id}, c.url, c.hash, {depth}, {page.Id}, {pending}
            FROM unnest({urls}, {hashes}) WITH ORDINALITY AS c(url, hash, ord)
            WHERE NOT EXISTS (SELECT 1 FROM pages p WHERE p.job_id = {job.Id} AND p.url_hash = c.hash)
            ORDER BY c.ord
            LIMIT {remaining}
            ON CONFLICT (job_id, url_hash) DO NOTHING
            """, cancellationToken);
    }
}
