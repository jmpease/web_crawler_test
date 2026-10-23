using System.Net;
using System.Text;
using Crawler.Domain;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Crawler.Infrastructure.Crawling;

/// <summary>
/// Fetches one page through the resilience pipeline, following redirects itself so every hop is checked against the
/// start host before it is requested (D-8). Never throws for HTTP outcomes; only caller cancellation escapes.
/// </summary>
public sealed class PageFetcher(HttpClient client, IOptions<FetcherOptions> options)
{
    public const int MaxRedirects = 5;

    private readonly FetcherOptions _options = options.Value;

    public async Task<FetchResult> FetchAsync(Uri url, string startHost, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Uri> { url };
        var current = url;
        for (var redirects = 0; ; redirects++)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (IsRequestFailure(ex, cancellationToken))
            {
                return FetchResult.Failed(current, null, Describe(ex));
            }

            using (response)
            {
                if (!IsRedirect(response.StatusCode))
                {
                    return await ReadPageAsync(current, response, cancellationToken);
                }

                var status = (int)response.StatusCode;
                if (!UrlNormalizer.TryNormalize(response.Headers.Location?.OriginalString, current, out var next))
                {
                    return FetchResult.Failed(current, status, "Redirect without a valid http(s) Location.");
                }

                if (!LinkAnalysis.IsSameHost(next!, startHost))
                {
                    return FetchResult.Skipped(next!, status, null, $"Redirect off-host to {next!.Host}; not followed.");
                }

                if (redirects == MaxRedirects)
                {
                    return FetchResult.Failed(current, status, $"More than {MaxRedirects} redirects.");
                }

                if (!visited.Add(next!))
                {
                    return FetchResult.Failed(current, status, $"Redirect loop at {next}.");
                }

                current = next!;
            }
        }
    }

    private async Task<FetchResult> ReadPageAsync(Uri url, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return FetchResult.Failed(url, status, $"HTTP {status}.");
        }

        var contentType = response.Content.Headers.ContentType;
        var mediaType = contentType?.MediaType;
        if (mediaType is not ("text/html" or "application/xhtml+xml"))
        {
            return FetchResult.Skipped(url, status, mediaType, $"Not HTML ({mediaType ?? "no content type"}).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.BodyTimeout);
        try
        {
            var body = await ReadBoundedAsync(response.Content, timeout.Token);
            return body is null
                ? FetchResult.Skipped(url, status, mediaType, $"Body larger than {_options.MaxBodyBytes} bytes.")
                : FetchResult.Fetched(url, status, mediaType, Decode(body, contentType?.CharSet));
        }
        catch (Exception ex) when (IsRequestFailure(ex, cancellationToken))
        {
            return FetchResult.Failed(url, status, ex is OperationCanceledException ? "Timed out reading the body." : Describe(ex));
        }
    }

    /// <summary>Returns null when the body exceeds <see cref="FetcherOptions.MaxBodyBytes"/>.</summary>
    private async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaxBodyBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > _options.MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Decode(byte[] body, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
                // Unknown charset: fall back to UTF-8.
            }
        }

        return encoding.GetString(body);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Failures that end this page; cancellation by the caller (shutdown) is not one of them.</summary>
    private static bool IsRequestFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutRejectedException => "Timed out after retries.",
        BrokenCircuitException => "Circuit open for this host after repeated failures.",
        HttpRequestException http => $"Request failed: {http.Message}",
        _ => "Request timed out.",
    };
}
