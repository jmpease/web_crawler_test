using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers the DbContext. The connection string is read from the final configuration when the context is
    /// first resolved, so test hosts can override it.
    /// </summary>
    public static IServiceCollection AddCrawlerPersistence(this IServiceCollection services) =>
        services.AddDbContext<CrawlerDbContext>((sp, options) => options.UseNpgsql(GetConnectionString(sp)));

    public static string GetConnectionString(IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>().GetConnectionString("Crawler")
            ?? throw new InvalidOperationException("Connection string 'Crawler' is not configured.");

    /// <summary>
    /// Applies pending migrations. Only the API calls this at startup, so two processes never race to migrate (D-16).
    /// </summary>
    public static async Task MigrateCrawlerDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
