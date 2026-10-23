using Crawler.Infrastructure.Health;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

// The API owns migrations (D-16); the worker only reads and writes.
builder.Services.AddJobCrawler(builder.Configuration);
builder.Services.AddCrawlerMessaging(builder.Configuration, bus =>
{
    bus.AddConsumer<CrawlJobConsumer, CrawlJobConsumerDefinition>();
    bus.AddConsumer<CrawlJobFaultConsumer, CrawlJobFaultConsumerDefinition>();
});
builder.Services.AddCrawlerHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

await app.RunAsync();
