using System.Net;
using Crawler.Domain;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.IntegrationTests;

/// <summary>
/// Crawls the local fixture site (Fixtures/site, served by <see cref="StubSite"/>) on real PostgreSQL.
/// Site map: / → a, b, / (self), external; a → /, c, external; b → a, doc.pdf, missing.html; c → d, www (external).
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Docker")]
public sealed class JobCrawlerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Origin = "https://site.test";

    private readonly StubSite _site = StubSite.FromDirectory(Origin, FixturePath.Site);
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _services = BuildWorker(_site);
        await WithDb(async db =>
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE edges, pages, jobs");
        });
    }

    public Task DisposeAsync() => _services.DisposeAsync().AsTask();

    [Fact]
    public async Task Crawls_breadth_first_within_max_depth_once_per_url()
    {
        var jobId = await InsertJob(maxDepth: 2);

        await Crawl(jobId);

        Assert.Equal(
            [$"{Origin}/", $"{Origin}/a.html", $"{Origin}/b.html", $"{Origin}/c.html", $"{Origin}/doc.pdf", $"{Origin}/missing.html"],
            _site.Requests);

        await WithDb(async db =>
        {
            var job = await db.Jobs.SingleAsync();
            Assert.Equal((JobStatus.Completed, 6, 6), (job.Status, job.PagesEnqueued, job.PagesProcessed));
            Assert.NotNull(job.StartedAt);
            Assert.NotNull(job.CompletedAt);

            var pages = await db.Pages.ToDictionaryAsync(p => p.Url.Replace(Origin, ""));
            Assert.Equal((PageStatus.Fetched, 0), (pages["/"].Status, pages["/"].Depth));
            Assert.Equal((PageStatus.Fetched, 2, pages["/a.html"].Id), (pages["/c.html"].Status, pages["/c.html"].Depth, pages["/c.html"].ParentPageId));
            Assert.Equal((PageStatus.Skipped, pages["/b.html"].Id), (pages["/doc.pdf"].Status, pages["/doc.pdf"].ParentPageId));
            Assert.Equal((PageStatus.Failed, 404), (pages["/missing.html"].Status, pages["/missing.html"].HttpStatus));
            Assert.Null(pages["/doc.pdf"].DomainLinkRatio);
            Assert.False(pages.ContainsKey("/d.html"));
        });
    }

    [Fact]
    public async Task Records_link_counts_ratios_and_every_outgoing_link()
    {
        var jobId = await InsertJob(maxDepth: 2);

        await Crawl(jobId);

        await WithDb(async db =>
        {
            var pages = await db.Pages.ToDictionaryAsync(p => p.Url.Replace(Origin, ""));
            // Root: a, b, itself (#top) and external; the fragment duplicate and mailto: don't count.
            Assert.Equal((4, 3), (pages["/"].OutgoingLinks, pages["/"].SameDomainLinks));
            Assert.Equal(0.75, pages["/"].DomainLinkRatio);
            Assert.Equal(2.0 / 3, pages["/a.html"].DomainLinkRatio!.Value, 6);
            Assert.Equal(1.0, pages["/b.html"].DomainLinkRatio);
            Assert.Equal(0.5, pages["/c.html"].DomainLinkRatio);

            var rootLinks = await db.Edges.Where(e => e.FromPageId == pages["/"].Id).OrderBy(e => e.ToUrl).Select(e => e.ToUrl).ToListAsync();
            Assert.Equal(["https://external.test/", $"{Origin}/", $"{Origin}/a.html", $"{Origin}/b.html"], rootLinks);
            Assert.Equal(12, await db.Edges.CountAsync());
        });
    }

    [Fact]
    public async Task Stops_enqueueing_at_max_pages()
    {
        var jobId = await InsertJob(maxDepth: 2, maxPages: 3);

        await Crawl(jobId);

        Assert.Equal([$"{Origin}/", $"{Origin}/a.html", $"{Origin}/b.html"], _site.Requests);
        await WithDb(async db =>
        {
            var job = await db.Jobs.SingleAsync();
            Assert.Equal((JobStatus.Completed, 3, 3), (job.Status, job.PagesEnqueued, job.PagesProcessed));
        });
    }

    [Fact]
    public async Task Max_depth_0_fetches_only_the_start_page_but_keeps_its_links()
    {
        var jobId = await InsertJob(maxDepth: 0);

        await Crawl(jobId);

        Assert.Equal([$"{Origin}/"], _site.Requests);
        await WithDb(async db =>
        {
            Assert.Equal(1, await db.Pages.CountAsync());
            Assert.Equal(4, await db.Edges.CountAsync());
            Assert.Equal(JobStatus.Completed, (await db.Jobs.SingleAsync()).Status);
        });
    }

    [Fact]
    public async Task A_second_delivery_of_a_completed_job_changes_nothing()
    {
        var jobId = await InsertJob(maxDepth: 2);
        await Crawl(jobId);
        var requestsAfterFirst = _site.Requests.Count;
        var before = await WithDb(db => db.Jobs.AsNoTracking().SingleAsync());

        await Crawl(jobId);

        Assert.Equal(requestsAfterFirst, _site.Requests.Count);
        var after = await WithDb(db => db.Jobs.AsNoTracking().SingleAsync());
        Assert.Equal((before.Status, before.PagesEnqueued, before.PagesProcessed, before.CompletedAt),
            (after.Status, after.PagesEnqueued, after.PagesProcessed, after.CompletedAt));
    }

    [Fact]
    public async Task A_failed_start_page_fails_the_job_with_its_reason()
    {
        _site.Status($"{Origin}/", HttpStatusCode.InternalServerError);
        var jobId = await InsertJob(maxDepth: 2);

        await Crawl(jobId);

        var job = await WithDb(db => db.Jobs.AsNoTracking().SingleAsync());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("HTTP 500", job.FailureReason);
        Assert.Equal(1, job.PagesProcessed);
    }

    [Fact]
    public async Task A_non_html_start_page_fails_the_job()
    {
        var jobId = await InsertJob(maxDepth: 2, startPath: "/doc.pdf");

        await Crawl(jobId);

        var job = await WithDb(db => db.Jobs.AsNoTracking().SingleAsync());
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.StartsWith("Start page skipped: Not HTML (application/pdf)", job.FailureReason);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task A_dead_lettered_job_is_failed_unless_it_already_finished()
    {
        var stuckJobId = await InsertJob(maxDepth: 2);
        var finishedJobId = await InsertJob(maxDepth: 0);
        await Crawl(finishedJobId);

        var (stuckFailed, finishedFailed) = await WithDb(async db =>
        {
            var store = new CrawlStore(db);
            return (await store.FailAsync(stuckJobId, "poison", CancellationToken.None),
                await store.FailAsync(finishedJobId, "late fault", CancellationToken.None));
        });

        Assert.Equal((true, false), (stuckFailed, finishedFailed));
        var jobs = await WithDb(db => db.Jobs.AsNoTracking().ToDictionaryAsync(j => j.Id));
        Assert.Equal((JobStatus.Failed, "poison"), (jobs[stuckJobId].Status, jobs[stuckJobId].FailureReason));
        Assert.NotNull(jobs[stuckJobId].CompletedAt);
        Assert.Equal((JobStatus.Completed, null), (jobs[finishedJobId].Status, jobs[finishedJobId].FailureReason));
    }

    private async Task Crawl(Guid jobId)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobCrawler>().RunAsync(jobId, CancellationToken.None);
    }

    /// <summary>Stores a job and its root page the way the API does.</summary>
    private Task<Guid> InsertJob(int maxDepth, int maxPages = CrawlLimits.DefaultMaxPages, string startPath = "/") => WithDb(async db =>
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Url = $"{Origin}{startPath}",
            StartHost = "site.test",
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = JobStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            PagesEnqueued = 1,
        };
        db.Jobs.Add(job);
        db.Pages.Add(new Page { JobId = job.Id, Url = job.Url, UrlHash = UrlHash.Compute(job.Url), Depth = 0, Status = PageStatus.Pending });
        await db.SaveChangesAsync();
        return job.Id;
    });

    private async Task WithDb(Func<CrawlerDbContext, Task> action) => await WithDb(async db =>
    {
        await action(db);
        return true;
    });

    private async Task<T> WithDb<T>(Func<CrawlerDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CrawlerDbContext>());
    }

    private ServiceProvider BuildWorker(StubSite site)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Crawler"] = postgres.ConnectionString,
                ["Fetcher:RetryBaseDelay"] = "00:00:00.001",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddJobCrawler(configuration).ConfigurePrimaryHttpMessageHandler(() => site);
        return services.BuildServiceProvider();
    }
}
