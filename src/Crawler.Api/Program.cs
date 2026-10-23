using Crawler.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCrawlerPersistence(
    builder.Configuration.GetConnectionString("Crawler")
        ?? throw new InvalidOperationException("Connection string 'Crawler' is not configured."));

var app = builder.Build();

await app.Services.MigrateCrawlerDatabaseAsync(app.Lifetime.ApplicationStopping);

await app.RunAsync();
