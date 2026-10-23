using Crawler.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
// The API owns migrations (D-16); the worker only reads and writes.
builder.Services.AddCrawlerPersistence(
    builder.Configuration.GetConnectionString("Crawler")
        ?? throw new InvalidOperationException("Connection string 'Crawler' is not configured."));

var app = builder.Build();

await app.RunAsync();
