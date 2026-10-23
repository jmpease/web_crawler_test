using Crawler.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
// The API owns migrations (D-16); the worker only reads and writes.
builder.Services.AddCrawlerPersistence();

var app = builder.Build();

await app.RunAsync();
