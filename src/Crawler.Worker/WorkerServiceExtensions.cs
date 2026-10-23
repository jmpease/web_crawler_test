using Crawler.Infrastructure.Crawling;
using Crawler.Infrastructure.Persistence;

namespace Crawler.Worker;

public static class WorkerServiceExtensions
{
    /// <summary>
    /// Registers the crawl pipeline (store, fetcher, crawler). Returns the fetcher's client builder so tests can swap the
    /// primary handler for a stub.
    /// </summary>
    public static IHttpClientBuilder AddJobCrawler(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCrawlerPersistence();
        services.AddScoped<CrawlStore>();
        services.AddScoped<JobCrawler>();
        return services.AddCrawlerFetcher(configuration);
    }
}
