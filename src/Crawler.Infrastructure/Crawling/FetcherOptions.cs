namespace Crawler.Infrastructure.Crawling;

/// <summary>Settings from the "Fetcher" section.</summary>
public sealed class FetcherOptions
{
    public const string Section = "Fetcher";

    public string UserAgent { get; set; } = "JobCrawler/1.0 (job-based web crawler; same-host only)";

    /// <summary>Larger HTML bodies are Skipped rather than parsed.</summary>
    public int MaxBodyBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Reading the body, after the headers arrive; the resilience pipeline covers the request up to the headers.</summary>
    public TimeSpan BodyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>First retry delay; later retries back off exponentially with jitter.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
