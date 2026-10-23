using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Infrastructure.Messaging;

public static class MessagingExtensions
{
    /// <summary>
    /// Registers MassTransit on RabbitMQ with kebab-case endpoint names (a CrawlJobConsumer listens on "crawl-job").
    /// Settings come from the "RabbitMq" section. MassTransit also registers a bus health check.
    /// </summary>
    public static IServiceCollection AddCrawlerMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureConsumers = null)
    {
        var section = configuration.GetSection("RabbitMq");
        var host = section["Host"] ?? "localhost";
        var port = ushort.TryParse(section["Port"], out var configuredPort) ? configuredPort : (ushort)5672;
        var virtualHost = section["VirtualHost"] ?? "/";

        return services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            configureConsumers?.Invoke(bus);
            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(host, port, virtualHost, h =>
                {
                    h.Username(section["Username"] ?? "guest");
                    h.Password(section["Password"] ?? "guest");
                });
                rabbit.ConfigureEndpoints(context);
            });
        });
    }
}
