using Crawler.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Infrastructure.Health;

public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds the PostgreSQL check. The broker check comes from MassTransit, which registers one for the bus.
    /// </summary>
    public static IServiceCollection AddCrawlerHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddNpgSql(PersistenceExtensions.GetConnectionString, name: "postgres");
        return services;
    }
}
