using Crawler.Infrastructure.Messaging;
using MassTransit;

namespace Crawler.Worker;

public sealed class CrawlJobFaultConsumerDefinition : ConsumerDefinition<CrawlJobFaultConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<CrawlJobFaultConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseTransientDatabaseRetry();
}
