namespace Crawler.Domain;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
}

public enum PageStatus
{
    Pending,
    Fetched,
    Skipped,
    Failed,
}
