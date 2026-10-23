namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Asks a worker to crawl one job. Published once per job with the envelope's CorrelationId set to the job id;
/// delivery is at-least-once, so the consumer must tolerate duplicates (D-13).
/// </summary>
public sealed record CrawlJob(Guid JobId);
