using Crawler.Infrastructure.Messaging;
using MassTransit;

namespace Crawler.Worker;

/// <summary>Listens on "crawl-job". Each delivery runs (or resumes) the whole job; duplicates of a finished job are no-ops.</summary>
public sealed class CrawlJobConsumer(JobCrawler crawler, ILogger<CrawlJobConsumer> logger) : IConsumer<CrawlJob>
{
    public async Task Consume(ConsumeContext<CrawlJob> context)
    {
        var scope = new Dictionary<string, object?>
        {
            ["correlationId"] = context.Message.JobId,
            ["messageId"] = context.MessageId,
        };
        using (logger.BeginScope(scope))
        {
            await crawler.RunAsync(context.Message.JobId, context.CancellationToken);
        }
    }
}
