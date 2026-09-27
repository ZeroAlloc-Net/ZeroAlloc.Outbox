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
/// 'model'", or its first login with a connection timeout. On a busy CI runner, where several
/// containers start at once, that window lasts long enough to hit.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Recovery is complete"))
            .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

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

        return builder.ConnectionString;
    }
}
