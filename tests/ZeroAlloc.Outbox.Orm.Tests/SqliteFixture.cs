using System.Data.Async;
using System.Data.Async.Adapters;
using Microsoft.Data.Sqlite;
using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// A SQLite database living as long as the fixture, with the outbox schema
/// applied through the ORM's own <see cref="MigrationRunner"/>.
/// </summary>
/// <remarks>
/// Shared-cache in-memory rather than plain <c>:memory:</c>, because the
/// transactional tests need a second connection observing the same rows. The
/// keep-alive connection holds the database open, since a shared-cache database
/// is dropped when its last connection closes.
/// </remarks>
public sealed class SqliteFixture : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    public SqliteFixture()
    {
        ConnectionString = $"Data Source=outbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    public string ConnectionString { get; }

    /// <summary>Opens a raw provider connection onto the fixture's database.</summary>
    public async Task<SqliteConnection> OpenRawAsync(CancellationToken ct = default)
    {
        var raw = new SqliteConnection(ConnectionString);
        await raw.OpenAsync(ct).ConfigureAwait(false);
        return raw;
    }

    /// <summary>Opens a connection wrapped for ZeroAlloc.ORM.</summary>
    public async Task<IAsyncDbConnection> ConnectAsync(CancellationToken ct = default)
        => (await OpenRawAsync(ct).ConfigureAwait(false)).AsAsync();

    /// <summary>Applies the outbox schema the way an application would.</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var runner = new MigrationRunner(
                connection, OutboxOrmMigrations.Sqlite, new SqliteMigrationDialect());
            await runner.RunAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Counts rows, for assertions about what actually committed.</summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        var raw = await OpenRawAsync(ct).ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM OutboxMessages";
                var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _keepAlive.Dispose();
        return ValueTask.CompletedTask;
    }
}
