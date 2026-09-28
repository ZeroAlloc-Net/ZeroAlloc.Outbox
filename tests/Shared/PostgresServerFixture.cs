using System.Collections.Concurrent;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ZeroAlloc.Outbox.TestServers;

/// <summary>
/// One PostgreSQL container for a whole test class. Each test creates a database of its own
/// with <see cref="CreateDatabaseAsync"/>, so tests stay isolated without paying for a
/// container start each.
/// </summary>
/// <remarks>
/// <see cref="ClearPools"/> clears only the pools of the databases this fixture created. xUnit
/// runs test classes in parallel, and a process-wide <c>NpgsqlConnection.ClearAllPools</c> at
/// the end of one test would reach into the pools of every other class.
/// </remarks>
public sealed class PostgresServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly ConcurrentQueue<string> _databases = new();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Drops the pooled connections to the databases this fixture created since the last call,
    /// so tests do not pile up server sessions. Pools of other test classes are left alone.
    /// </summary>
    public void ClearPools()
    {
        while (_databases.TryDequeue(out var connectionString))
        {
            using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
        }
    }

    /// <summary>Creates a fresh, empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"outbox_{Guid.NewGuid():N}",
        };

        var admin = new NpgsqlConnection(_container.GetConnectionString());
        await using (admin.ConfigureAwait(false))
        {
            await admin.OpenAsync().ConfigureAwait(false);
            var create = admin.CreateCommand();
            await using (create.ConfigureAwait(false))
            {
                create.CommandText = $"CREATE DATABASE \"{builder.Database}\"";
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        _databases.Enqueue(builder.ConnectionString);
        return builder.ConnectionString;
    }
}
