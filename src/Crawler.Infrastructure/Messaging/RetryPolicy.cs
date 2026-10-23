using MassTransit;

namespace Crawler.Infrastructure.Messaging;

public static class RetryPolicy
{
    /// <summary>
    /// Three more attempts with exponential backoff (1 s up to 10 s) for transient database errors only. Anything else,
    /// or a fourth failure, moves the message to the endpoint's <c>_error</c> queue and publishes a <see cref="Fault{T}"/>.
    /// </summary>
    public static void UseTransientDatabaseRetry(this IReceiveEndpointConfigurator endpoint) =>
        endpoint.UseMessageRetry(retry =>
        {
            retry.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
            retry.Handle<Exception>(TransientErrors.IsTransient);
        });
}
