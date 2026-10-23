using System.Net.Sockets;
using Crawler.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Crawler.IntegrationTests;

public sealed class TransientErrorsTests
{
    [Fact]
    public void A_lost_database_connection_is_transient() =>
        Assert.True(TransientErrors.IsTransient(new NpgsqlException("connection lost", new SocketException())));

    [Fact]
    public void A_serialization_failure_wrapped_by_ef_is_transient() =>
        Assert.True(TransientErrors.IsTransient(new DbUpdateException("save failed", Postgres("40001"))));

    [Fact]
    public void A_dropped_connection_wrapped_by_efs_execution_strategy_is_transient() =>
        Assert.True(TransientErrors.IsTransient(new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new NpgsqlException("Failed to connect", new SocketException()))));

    [Fact]
    public void An_unresolvable_database_host_is_transient() =>
        Assert.True(TransientErrors.IsTransient(new SocketException((int)SocketError.HostNotFound)));

    [Fact]
    public void A_unique_violation_is_not_transient() =>
        Assert.False(TransientErrors.IsTransient(new DbUpdateException("save failed", Postgres("23505"))));

    [Fact]
    public void Errors_outside_the_database_are_not_transient()
    {
        Assert.False(TransientErrors.IsTransient(new InvalidOperationException("bug")));
        Assert.False(TransientErrors.IsTransient(new HttpRequestException("site down")));
        Assert.False(TransientErrors.IsTransient(new OperationCanceledException()));
    }

    private static PostgresException Postgres(string sqlState) => new("error", "ERROR", "ERROR", sqlState);
}
