using System.Net;
using Crawler.Domain;
using Crawler.Infrastructure.Crawling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.IntegrationTests;

/// <summary>The real typed client and resilience pipeline over a stub handler; no network, no Docker.</summary>
public sealed class PageFetcherTests
{
    private const string Host = "site.test";

    [Fact]
    public async Task Follows_a_same_host_redirect_and_reports_where_it_landed()
    {
        var site = new StubSite()
            .Redirect("https://site.test/old", "/new")
            .Html("https://site.test/new", "<a href='/x'>x</a>");

        var result = await Fetch(site, "https://site.test/old");

        Assert.Equal((PageStatus.Fetched, "https://site.test/new", 200), (result.Status, result.FinalUrl.AbsoluteUri, result.HttpStatus));
        Assert.Contains("/x", result.Html);
        Assert.Equal(["https://site.test/old", "https://site.test/new"], site.Requests);
    }

    [Fact]
    public async Task Skips_an_off_host_redirect_without_requesting_it()
    {
        var site = new StubSite().Redirect("https://site.test/out", "https://other.test/", HttpStatusCode.Found);

        var result = await Fetch(site, "https://site.test/out");

        Assert.Equal(PageStatus.Skipped, result.Status);
        Assert.Contains("off-host", result.Error);
        Assert.Equal(["https://site.test/out"], site.Requests);
    }

    [Fact]
    public async Task Fails_on_a_redirect_loop()
    {
        var site = new StubSite()
            .Redirect("https://site.test/a", "/b")
            .Redirect("https://site.test/b", "/a");

        var result = await Fetch(site, "https://site.test/a");

        Assert.Equal(PageStatus.Failed, result.Status);
        Assert.Contains("loop", result.Error);
    }

    [Fact]
    public async Task Fails_after_more_than_five_redirects()
    {
        var site = new StubSite();
        for (var i = 0; i <= PageFetcher.MaxRedirects; i++)
        {
            site.Redirect($"https://site.test/r{i}", $"/r{i + 1}");
        }

        var result = await Fetch(site, "https://site.test/r0");

        Assert.Equal(PageStatus.Failed, result.Status);
        Assert.Contains("redirects", result.Error);
        Assert.Equal(PageFetcher.MaxRedirects + 1, site.Requests.Count);
    }

    [Fact]
    public async Task Retries_a_server_error_three_times_then_fails_without_throwing()
    {
        var site = new StubSite().Status("https://site.test/down", HttpStatusCode.ServiceUnavailable);

        var result = await Fetch(site, "https://site.test/down");

        Assert.Equal((PageStatus.Failed, 503), (result.Status, result.HttpStatus));
        Assert.Equal(4, site.Requests.Count);
    }

    [Fact]
    public async Task Fails_on_a_network_error_after_retries_without_throwing()
    {
        var site = new StubSite().Respond("https://site.test/", () => throw new HttpRequestException("connection refused"));

        var result = await Fetch(site, "https://site.test/");

        Assert.Equal(PageStatus.Failed, result.Status);
        Assert.Contains("connection refused", result.Error);
        Assert.Equal(4, site.Requests.Count);
    }

    [Fact]
    public async Task Does_not_retry_a_client_error()
    {
        var site = new StubSite();

        var result = await Fetch(site, "https://site.test/missing");

        Assert.Equal((PageStatus.Failed, 404), (result.Status, result.HttpStatus));
        Assert.Single(site.Requests);
    }

    [Fact]
    public async Task Skips_a_non_html_response()
    {
        var site = StubSite.FromDirectory("https://site.test", FixturePath.Site);

        var result = await Fetch(site, "https://site.test/doc.pdf");

        Assert.Equal((PageStatus.Skipped, "application/pdf"), (result.Status, result.ContentType));
        Assert.Null(result.Html);
    }

    [Fact]
    public async Task Sends_a_descriptive_user_agent()
    {
        var site = new StubSite().Html("https://site.test/", "<p>hi</p>");

        await Fetch(site, "https://site.test/");

        Assert.StartsWith("JobCrawler/1.0", site.LastUserAgent);
    }

    private static async Task<FetchResult> Fetch(StubSite site, string url)
    {
        await using var services = BuildServices(site);
        return await services.GetRequiredService<PageFetcher>().FetchAsync(new Uri(url), Host, CancellationToken.None);
    }

    private static ServiceProvider BuildServices(StubSite site)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fetcher:RetryBaseDelay"] = "00:00:00.001" })
            .Build();
        var services = new ServiceCollection();
        services.AddCrawlerFetcher(configuration).ConfigurePrimaryHttpMessageHandler(() => site);
        return services.BuildServiceProvider();
    }
}

public static class FixturePath
{
    public static string Site => Path.Combine(AppContext.BaseDirectory, "Fixtures", "site");
}
