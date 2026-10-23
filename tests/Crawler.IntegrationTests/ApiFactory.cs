using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Crawler.IntegrationTests;

/// <summary>Hosts the API in memory with MassTransit's in-memory test harness in place of RabbitMQ.</summary>
public class ApiFactory(string connectionString, bool migrateOnStartup) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Crawler"] = connectionString,
            ["Database:MigrateOnStartup"] = migrateOnStartup.ToString(),
        }));
        builder.ConfigureTestServices(services => services.AddMassTransitTestHarness());
    }
}

/// <summary>An API with no reachable database, for requests rejected before any query runs.</summary>
public sealed class OfflineApiFactory() : ApiFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none", migrateOnStartup: false);
