using Npgsql;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Decides which consumer exceptions are worth another delivery attempt: only transient database errors (D-9, D-28).
/// HTTP problems never get here because the fetcher turns them into page results.
/// </summary>
public static class TransientErrors
{
    /// <summary>
    /// Looks for an <see cref="NpgsqlException"/> anywhere in the chain: EF wraps it in <c>DbUpdateException</c> from
    /// SaveChanges, and its default execution strategy wraps it in an <see cref="InvalidOperationException"/> elsewhere.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException npgsql)
            {
                return npgsql.IsTransient;
            }
        }

        return false;
    }
}
