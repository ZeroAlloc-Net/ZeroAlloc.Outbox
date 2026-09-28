using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace ZeroAlloc.Outbox.TestServers;

/// <summary>
/// One SQL Server container for a whole test class. Each test creates a database of its own
/// with <see cref="CreateDatabaseAsync"/>, so tests stay isolated without paying for a
/// container start each.
/// </summary>
/// <remarks>
/// SQL Server 2022 creates databases at compatibility level 160. The EF Core claim needs at
/// least 130, because EF Core translates a <c>Contains</c> over a parameter list to
/// <c>OPENJSON</c>.
/// <para>
/// The container counts as started only once SQL Server logs <c>Recovery is complete</c>.
/// The builder's own readiness check, a <c>SELECT 1</c> through sqlcmd, succeeds while startup
/// is still upgrading msdb and building tempdb from model. A test that starts in that window
/// fails its first <c>CREATE DATABASE</c> with "Could not obtain exclusive lock on database
/// 'model'". On a busy CI runner, where several containers start at once, that window lasts
/// long enough to hit.
/// </para>
/// <para>
/// <see cref="ClearPools"/> clears only the pools of the databases this fixture created. xUnit
/// runs test classes in parallel, so a process-wide <c>SqlConnection.ClearAllPools</c> at the
/// end of one test shuts down the pool another class is opening a connection on. SqlClient
/// then fails that open at once with "Timeout expired. The timeout period elapsed prior to
/// obtaining a connection from the pool".
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Recovery is complete"))
            .Build();

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
            using var connection = new SqlConnection(connectionString);
            SqlConnection.ClearPool(connection);
        }
    }

    /// <summary>Creates a fresh, empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = $"Outbox_{Guid.NewGuid():N}",
        };

        var master = new SqlConnection(_container.GetConnectionString());
        await using (master.ConfigureAwait(false))
        {
            await master.OpenAsync().ConfigureAwait(false);
            var create = master.CreateCommand();
            await using (create.ConfigureAwait(false))
            {
                create.CommandText = $"CREATE DATABASE [{builder.InitialCatalog}]";
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        _databases.Enqueue(builder.ConnectionString);
        return builder.ConnectionString;
    }
}
