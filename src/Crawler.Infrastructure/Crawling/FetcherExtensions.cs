using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Crawler.Infrastructure.Crawling;

public static class FetcherExtensions
{
    /// <summary>
    /// Registers <see cref="PageFetcher"/> as a typed client: no automatic redirects, a descriptive User-Agent, and the
    /// standard resilience handler (10 s per attempt, 20 s total, 3 retries with exponential backoff and jitter on
    /// 5xx, 408, 429, network errors and timeouts), keyed per host so one failing site can't open the circuit for others.
    /// Options are read here, at registration, because the resilience settings are fixed when the client is built.
    /// </summary>
    public static IHttpClientBuilder AddCrawlerFetcher(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(FetcherOptions.Section).Get<FetcherOptions>() ?? new FetcherOptions();
        services.AddSingleton(Options.Create(options));

        var client = services
            .AddHttpClient<PageFetcher>(http => http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });

        client.AddStandardResilienceHandler(resilience =>
        {
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
            resilience.Retry.MaxRetryAttempts = 3;
            resilience.Retry.BackoffType = DelayBackoffType.Exponential;
            resilience.Retry.UseJitter = true;
            resilience.Retry.Delay = options.RetryBaseDelay;
        }).SelectPipelineByAuthority();

        return client;
    }
}
