using Crawler.Domain;

namespace Crawler.Infrastructure.Crawling;

/// <summary>
/// The outcome of fetching one page. HTTP failures are values, not exceptions, so they never reach message retry (D-9).
/// <see cref="Html"/> is set only when <see cref="Status"/> is Fetched.
/// </summary>
public sealed record FetchResult(PageStatus Status, Uri FinalUrl, int? HttpStatus, string? ContentType, string? Html, string? Error)
{
    public static FetchResult Fetched(Uri finalUrl, int httpStatus, string contentType, string html) =>
        new(PageStatus.Fetched, finalUrl, httpStatus, contentType, html, null);

    public static FetchResult Skipped(Uri finalUrl, int? httpStatus, string? contentType, string reason) =>
        new(PageStatus.Skipped, finalUrl, httpStatus, contentType, null, reason);

    public static FetchResult Failed(Uri finalUrl, int? httpStatus, string reason) =>
        new(PageStatus.Failed, finalUrl, httpStatus, null, null, reason);
}
