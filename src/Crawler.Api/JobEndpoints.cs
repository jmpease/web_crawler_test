using Crawler.Domain;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crawler.Api;

public static partial class JobEndpoints
{
    private const string LoggerCategory = "Crawler.Api.Jobs";
    private const int MaxPageSize = 100;

    public static RouteGroupBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").AddEndpointFilter(WithJobLogScope);
        group.MapPost("/", CreateJob);
        group.MapGet("/", ListJobs);
        group.MapGet("/{id:guid}", GetJob);
        group.MapGet("/{id:guid}/tree", GetJobTree);
        return group;
    }

    /// <summary>
    /// Inserts the job and its root page in one transaction, then publishes <see cref="CrawlJob"/>.
    /// A crash between the two leaves the job Pending with no message (the outbox gap, PLAN step 6).
    /// </summary>
    private static async Task<Results<Accepted<CreateJobResponse>, ValidationProblem>> CreateJob(
        CreateJobRequest request,
        CrawlerDbContext db,
        IPublishEndpoint publisher,
        IOptions<JobOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = JobRequestValidation.Validate(request, out var url);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var job = NewJob(url!, request.MaxDepth ?? CrawlLimits.DefaultMaxDepth, options.Value.MaxPages);
        db.Jobs.Add(job);
        db.Pages.Add(RootPage(job));
        await db.SaveChangesAsync(cancellationToken);

        var logger = loggerFactory.CreateLogger(LoggerCategory);
        using (logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = job.Id }))
        {
            // Not tied to the request token: once the job is saved, a client disconnect must not strand it unpublished.
            await publisher.Publish(new CrawlJob(job.Id), context => context.CorrelationId = job.Id, CancellationToken.None);
            LogJobCreated(logger, job.Url, job.MaxDepth);
        }

        return TypedResults.Accepted($"/api/jobs/{job.Id}", new CreateJobResponse(job.Id));
    }

    private static async Task<Results<Ok<JobPageResponse>, ValidationProblem>> ListJobs(
        CrawlerDbContext db,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 20)
    {
        var errors = JobRequestValidation.ValidatePaging(page, pageSize, MaxPageSize);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var total = await db.Jobs.CountAsync(cancellationToken);
        var jobs = await db.Jobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new JobPageResponse(jobs.Select(JobResponse.From).ToList(), page, pageSize, total));
    }

    private static async Task<Results<Ok<JobResponse>, ProblemHttpResult>> GetJob(
        Guid id,
        CrawlerDbContext db,
        CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        return job is null ? JobNotFound(id) : TypedResults.Ok(JobResponse.From(job));
    }

    /// <summary>One query for pages and one for links; the tree is assembled in memory (no N+1).</summary>
    private static async Task<Results<Ok<JobTreeResponse>, ProblemHttpResult>> GetJobTree(
        Guid id,
        CrawlerDbContext db,
        CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new { j.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return JobNotFound(id);
        }

        var pages = await db.Pages.AsNoTracking()
            .Where(p => p.JobId == id)
            .Select(p => new TreePage(p.Id, p.ParentPageId, p.Url, p.Depth, p.Status, p.DomainLinkRatio, p.Error))
            .ToListAsync(cancellationToken);
        var links = await db.Edges.AsNoTracking()
            .Where(e => e.JobId == id)
            .Select(e => new TreeLink(e.FromPageId, e.ToUrl, e.IsSameDomain))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new JobTreeResponse(id, job.Status, PageTree.Build(pages, links)));
    }

    private static Job NewJob(Uri url, int maxDepth, int maxPages) => new()
    {
        Id = Guid.NewGuid(),
        Url = url.AbsoluteUri,
        StartHost = url.Host,
        MaxDepth = maxDepth,
        MaxPages = maxPages,
        Status = JobStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        PagesEnqueued = 1,
    };

    private static Page RootPage(Job job) => new()
    {
        JobId = job.Id,
        Url = job.Url,
        UrlHash = UrlHash.Compute(job.Url),
        Depth = 0,
        Status = PageStatus.Pending,
    };

    private static ProblemHttpResult JobNotFound(Guid id) =>
        TypedResults.Problem(title: "Job not found", detail: $"No crawl job with id {id}.", statusCode: StatusCodes.Status404NotFound);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created crawl job for {Url} with max depth {MaxDepth}")]
    private static partial void LogJobCreated(ILogger logger, string url, int maxDepth);

    /// <summary>Puts the route's job id on every log written while handling the request.</summary>
    private static async ValueTask<object?> WithJobLogScope(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var routeId = context.HttpContext.Request.RouteValues["id"]?.ToString();
        if (!Guid.TryParse(routeId, out var jobId))
        {
            return await next(context);
        }

        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        using (logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = jobId }))
        {
            return await next(context);
        }
    }
}
