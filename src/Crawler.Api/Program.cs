using System.Text.Json.Serialization;
using Crawler.Api;
using Crawler.Infrastructure.Health;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddCrawlerPersistence();
builder.Services.AddCrawlerMessaging(builder.Configuration);
builder.Services.AddCrawlerHealthChecks();
builder.Services.Configure<JobOptions>(builder.Configuration.GetSection(JobOptions.Section));
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

app.MapHealthChecks("/health");
app.MapJobEndpoints();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.MigrateCrawlerDatabaseAsync(app.Lifetime.ApplicationStopping);
}

await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
