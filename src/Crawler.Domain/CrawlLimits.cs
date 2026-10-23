namespace Crawler.Domain;

public static class CrawlLimits
{
    /// <summary>The start page is depth 0.</summary>
    public const int DefaultMaxDepth = 2;

    public const int MaxAllowedDepth = 5;

    /// <summary>Pages enqueued per job, including the root, unless configured otherwise.</summary>
    public const int DefaultMaxPages = 200;

    public const int MaxUrlLength = 2048;
}
