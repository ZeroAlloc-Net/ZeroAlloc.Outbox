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
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

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
