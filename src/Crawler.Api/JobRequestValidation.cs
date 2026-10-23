using Crawler.Domain;

namespace Crawler.Api;

/// <summary>Request checks that produce ValidationProblem errors keyed by field name.</summary>
public static class JobRequestValidation
{
    /// <summary>On success, <paramref name="url"/> is the normalized start URL.</summary>
    public static Dictionary<string, string[]> Validate(CreateJobRequest request, out Uri? url)
    {
        var errors = new Dictionary<string, string[]>();
        url = null;

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            errors["url"] = ["A start URL is required."];
        }
        else if (request.Url.Length > CrawlLimits.MaxUrlLength)
        {
            errors["url"] = [$"The URL must be at most {CrawlLimits.MaxUrlLength} characters."];
        }
        else if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var parsed) || !UrlNormalizer.IsHttp(parsed))
        {
            errors["url"] = ["The URL must be an absolute http or https URL."];
        }
        else
        {
            url = UrlNormalizer.Normalize(parsed);
        }

        if (request.MaxDepth is < 0 or > CrawlLimits.MaxAllowedDepth)
        {
            errors["maxDepth"] = [$"maxDepth must be between 0 and {CrawlLimits.MaxAllowedDepth}."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidatePaging(int page, int pageSize, int maxPageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (pageSize < 1 || pageSize > maxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {maxPageSize}."];
        }

        return errors;
    }
}
