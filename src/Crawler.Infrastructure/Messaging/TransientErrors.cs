using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Decides which consumer exceptions are worth another delivery attempt: only transient database errors (D-9).
/// HTTP problems never get here because the fetcher turns them into page results.
/// </summary>
public static class TransientErrors
{
    public static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,
        DbUpdateException { InnerException: NpgsqlException inner } => inner.IsTransient,
        _ => false,
    };
}
