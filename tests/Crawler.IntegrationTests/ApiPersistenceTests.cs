using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crawler.Api;
using Crawler.Domain;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Docker")]
public sealed class ApiPersistenceTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ApiFactory _factory = new(postgres.ConnectionString, migrateOnStartup: true);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await WithDb(db => db.Database.ExecuteSqlRawAsync("TRUNCATE edges, pages, jobs"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Creating_a_job_stores_it_with_a_pending_root_page_and_publishes_one_message()
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new { url = "HTTPS://Example.COM:443", maxDepth = 3 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await response.Content.ReadFromJsonAsync<CreateJobResponse>(Json))!.JobId;
        Assert.Equal($"/api/jobs/{jobId}", response.Headers.Location?.OriginalString);

        await WithDb(async db =>
        {
            var job = await db.Jobs.SingleAsync();
            Assert.Equal((jobId, "https://example.com/", "example.com", 3, 200, JobStatus.Pending, 1),
                (job.Id, job.Url, job.StartHost, job.MaxDepth, job.MaxPages, job.Status, job.PagesEnqueued));
            var root = await db.Pages.SingleAsync();
            Assert.Equal((jobId, "https://example.com/", 0, (long?)null, PageStatus.Pending),
                (root.JobId, root.Url, root.Depth, root.ParentPageId, root.Status));
            Assert.Equal(UrlHash.Compute("https://example.com/"), root.UrlHash);
        });

        var harness = _factory.Services.GetTestHarness();
        var published = Assert.Single(harness.Published.Select<CrawlJob>());
        Assert.Equal(jobId, published.Context.Message.JobId);
        Assert.Equal(jobId, published.Context.CorrelationId);
    }

    [Fact]
    public async Task Max_depth_defaults_to_2()
    {
        var jobId = await CreateJob("https://example.com/");

        var job = await _client.GetFromJsonAsync<JobResponse>($"/api/jobs/{jobId}", Json);

        Assert.Equal(2, job!.MaxDepth);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal((1, 0), (job.PagesEnqueued, job.PagesProcessed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/tree")]
    public async Task Unknown_job_is_a_problem_details_404(string suffix)
    {
        var response = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}{suffix}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task History_lists_most_recent_first_with_a_total()
    {
        var first = await CreateJob("https://a.test/");
        var second = await CreateJob("https://b.test/");
        var third = await CreateJob("https://c.test/");

        var page1 = await _client.GetFromJsonAsync<JobPageResponse>("/api/jobs?page=1&pageSize=2", Json);
        var page2 = await _client.GetFromJsonAsync<JobPageResponse>("/api/jobs?page=2&pageSize=2", Json);

        Assert.Equal(3, page1!.Total);
        Assert.Equal([third, second], page1.Items.Select(j => j.Id));
        Assert.Equal([first], page2!.Items.Select(j => j.Id));
    }

    [Fact]
    public async Task Tree_nests_stored_pages_and_their_links()
    {
        var jobId = await CreateJob("https://example.com/");
        await WithDb(async db =>
        {
            var root = await db.Pages.SingleAsync();
            root.Status = PageStatus.Fetched;
            root.DomainLinkRatio = 0.5;
            var child = NewPage(jobId, "https://example.com/about", depth: 1, parent: root.Id);
            db.Pages.Add(child);
            await db.SaveChangesAsync();
            db.Edges.AddRange(
                NewEdge(jobId, root.Id, "https://example.com/about", isSameDomain: true),
                NewEdge(jobId, root.Id, "https://other.test/", isSameDomain: false));
            await db.SaveChangesAsync();
        });

        var tree = await _client.GetFromJsonAsync<JobTreeResponse>($"/api/jobs/{jobId}/tree", Json);

        var root = tree!.Root!;
        Assert.Equal((PageStatus.Fetched, 0.5), (root.Status, root.DomainLinkRatio));
        Assert.Equal(["https://example.com/about", "https://other.test/"], root.OutgoingLinks.Select(l => l.Url));
        var child = Assert.Single(root.Children);
        Assert.Equal(("https://example.com/about", 1, PageStatus.Pending), (child.Url, child.Depth, child.Status));
    }

    [Fact]
    public async Task Health_reports_healthy_when_database_and_bus_are_up()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreateJob(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/jobs", new { url });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateJobResponse>(Json))!.JobId;
    }

    private async Task WithDb(Func<CrawlerDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<CrawlerDbContext>());
    }

    private static Page NewPage(Guid jobId, string url, int depth, long parent) => new()
    {
        JobId = jobId,
        Url = url,
        UrlHash = UrlHash.Compute(url),
        Depth = depth,
        ParentPageId = parent,
        Status = PageStatus.Pending,
    };

    private static Edge NewEdge(Guid jobId, long from, string url, bool isSameDomain) => new()
    {
        JobId = jobId,
        FromPageId = from,
        ToUrl = url,
        ToUrlHash = UrlHash.Compute(url),
        IsSameDomain = isSameDomain,
    };
}
