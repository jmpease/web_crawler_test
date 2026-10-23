using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    public static IServiceCollection AddCrawlerPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<CrawlerDbContext>(options => options.UseNpgsql(connectionString));

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
