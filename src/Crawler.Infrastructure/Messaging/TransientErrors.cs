using System.Net.Sockets;
using Npgsql;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Decides which consumer exceptions are worth another delivery attempt: only transient database errors (D-9, D-28, D-29).
/// HTTP problems never get here because the fetcher turns them into page results.
/// </summary>
public static class TransientErrors
{
    /// <summary>
    /// Looks for an <see cref="NpgsqlException"/> anywhere in the chain: EF wraps it in <c>DbUpdateException</c> from
    /// SaveChanges, and its default execution strategy wraps it in an <see cref="InvalidOperationException"/> elsewhere.
    /// A <see cref="SocketException"/> also counts: Npgsql lets a failed lookup of the database host through unwrapped
    /// (the compose <c>db</c> name doesn't resolve while its container is down). The fetcher never throws one.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case NpgsqlException npgsql:
                    return npgsql.IsTransient;
                case SocketException:
                    return true;
            }
        }

        return false;
    }
}
