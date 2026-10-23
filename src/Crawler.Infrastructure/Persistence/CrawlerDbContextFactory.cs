using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crawler.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>, so migrations can be generated without starting the API.
/// Generating a migration never connects; the connection string matters only for <c>dotnet ef database update</c>.
/// </summary>
public sealed class CrawlerDbContextFactory : IDesignTimeDbContextFactory<CrawlerDbContext>
{
    public CrawlerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Crawler")
            ?? "Host=localhost;Database=crawler;Username=crawler;Password=crawler_dev";
        var options = new DbContextOptionsBuilder<CrawlerDbContext>().UseNpgsql(connectionString).Options;
        return new CrawlerDbContext(options);
    }
}
