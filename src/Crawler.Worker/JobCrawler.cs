using Crawler.Domain;
using Crawler.Infrastructure.Crawling;
using Crawler.Infrastructure.Persistence;

namespace Crawler.Worker;

/// <summary>
/// Crawls one job breadth-first. The frontier lives in the database, so running this again for the same job resumes
/// a Running job and does nothing for a finished one.
/// </summary>
public sealed partial class JobCrawler(CrawlStore store, PageFetcher fetcher, ILogger<JobCrawler> logger)
{
    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await store.StartAsync(jobId, cancellationToken);
        if (job is null)
        {
            LogJobNotRunnable(logger);
            return;
        }

        LogJobStarted(logger, job.MaxDepth);
        while (await store.NextPendingPageAsync(jobId, cancellationToken) is { } page)
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["pageId"] = page.Id }))
            {
                await CrawlPageAsync(job, page, cancellationToken);
            }
        }

        await FinishAsync(jobId, cancellationToken);
    }

    private async Task CrawlPageAsync(CrawlJobState job, PendingPage page, CancellationToken cancellationToken)
    {
        var fetch = await fetcher.FetchAsync(new Uri(page.Url), job.StartHost, cancellationToken);
        // Relative links resolve against where the page actually landed after redirects.
        var links = fetch.Html is null ? null : HtmlLinkExtractor.Analyze(fetch.Html, fetch.FinalUrl, job.StartHost);
        var finalUrl = fetch.FinalUrl.AbsoluteUri == page.Url ? null : fetch.FinalUrl.AbsoluteUri;
        var outcome = new PageOutcome(fetch.Status, finalUrl, fetch.HttpStatus, fetch.ContentType, fetch.Error, links);

        if (await store.SavePageAsync(job, page, outcome, cancellationToken))
        {
            LogPageCrawled(logger, page.Url, page.Depth, fetch.Status, fetch.HttpStatus, links?.OutgoingLinks, fetch.Error);
        }
        else
        {
            LogPageAlreadyCrawled(logger, page.Url);
        }
    }

    /// <summary>
    /// The job fails when its start page couldn't be crawled (failed, or skipped as non-HTML or redirected off-host),
    /// since there is nothing to show; otherwise per-page statuses tell the story (D-25).
    /// </summary>
    private async Task FinishAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var root = await store.GetRootPageAsync(jobId, cancellationToken);
        if (root.Status != PageStatus.Fetched)
        {
            var reason = $"Start page {root.Status.ToString().ToLowerInvariant()}: {root.Error}";
            await store.FinishAsync(jobId, JobStatus.Failed, reason, cancellationToken);
            LogJobFailed(logger, reason);
        }
        else
        {
            await store.FinishAsync(jobId, JobStatus.Completed, null, cancellationToken);
            LogJobCompleted(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job is missing or already finished; nothing to do")]
    private static partial void LogJobNotRunnable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Crawling job with max depth {MaxDepth}")]
    private static partial void LogJobStarted(ILogger logger, int maxDepth);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Crawled {Url} at depth {Depth}: {Status} (HTTP {HttpStatus}, {OutgoingLinks} links) {Error}")]
    private static partial void LogPageCrawled(
        ILogger logger, string url, int depth, PageStatus status, int? httpStatus, int? outgoingLinks, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Url} was already crawled by another delivery; result discarded")]
    private static partial void LogPageAlreadyCrawled(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job failed: {Reason}")]
    private static partial void LogJobFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job completed")]
    private static partial void LogJobCompleted(ILogger logger);
}
