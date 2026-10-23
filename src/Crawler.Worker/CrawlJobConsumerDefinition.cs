using Crawler.Infrastructure.Messaging;
using MassTransit;

namespace Crawler.Worker;

public sealed class CrawlJobConsumerDefinition : ConsumerDefinition<CrawlJobConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<CrawlJobConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseTransientDatabaseRetry();
}
