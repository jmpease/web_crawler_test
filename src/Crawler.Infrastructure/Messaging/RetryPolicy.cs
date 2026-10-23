using MassTransit;

namespace Crawler.Infrastructure.Messaging;

public static class RetryPolicy
{
    /// <summary>
    /// Five more attempts with exponential backoff (1 s up to 30 s, about a minute in all) for transient database errors only. Anything else,
    /// or a sixth failure, moves the message to the endpoint's <c>_error</c> queue and publishes a <see cref="Fault{T}"/>.
    /// </summary>
    public static void UseTransientDatabaseRetry(this IReceiveEndpointConfigurator endpoint) =>
        endpoint.UseMessageRetry(retry =>
        {
            retry.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
            retry.Handle<Exception>(TransientErrors.IsTransient);
        });
}
