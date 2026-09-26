using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ZeroAlloc.Outbox.TestServers;

/// <summary>
/// One PostgreSQL container for a whole test class. Each test creates a database of its own
/// with <see cref="CreateDatabaseAsync"/>, so tests stay isolated without paying for a
/// container start each.
/// </summary>
public sealed class PostgresServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

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

        return builder.ConnectionString;
    }
}
