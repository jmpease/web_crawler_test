using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using MassTransit;

namespace Crawler.Worker;

/// <summary>
/// Listens on "crawl-job-fault". When a CrawlJob message is dead-lettered (non-transient error, or retries exhausted),
/// marks its job Failed so it can't stay Running forever (D-7, D-13).
/// </summary>
public sealed partial class CrawlJobFaultConsumer(CrawlStore store, ILogger<CrawlJobFaultConsumer> logger) : IConsumer<Fault<CrawlJob>>
{
    public async Task Consume(ConsumeContext<Fault<CrawlJob>> context)
    {
        var fault = context.Message;
        var scope = new Dictionary<string, object?>
        {
            ["correlationId"] = fault.Message.JobId,
            ["messageId"] = context.MessageId,
            ["faultedMessageId"] = fault.FaultedMessageId,
        };
        using (logger.BeginScope(scope))
        {
            var exception = fault.Exceptions.FirstOrDefault();
            var reason = exception is null
                ? "Crawl aborted by an unrecoverable worker error"
                : $"Crawl aborted by an unrecoverable worker error: {exception.ExceptionType}: {exception.Message}";

            var failed = await store.FailAsync(fault.Message.JobId, reason, context.CancellationToken);
            LogJobFaulted(logger, exception?.ExceptionType, failed);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "CrawlJob message dead-lettered ({ExceptionType}); job marked Failed: {Failed}")]
    private static partial void LogJobFaulted(ILogger logger, string? exceptionType, bool failed);
}
