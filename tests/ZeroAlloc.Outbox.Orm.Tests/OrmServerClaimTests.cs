using System.Data.Async;
using System.Data.Async.Adapters;
using System.Data.Common;
using AwesomeAssertions;
using Xunit;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// <see cref="OutboxServerClaimTests"/> for <see cref="OrmOutboxStore"/>, each store on a
/// provider connection of its own, with the schema applied through the ORM's
/// <see cref="MigrationRunner"/>.
/// </summary>
public abstract class OrmServerClaimTests : OutboxServerClaimTests
{
    private readonly List<IAsyncDbConnection> _connections = [];
    private string _connectionString = string.Empty;

    /// <summary>The claim spelling under test.</summary>
    protected abstract OutboxOrmDialect Dialect { get; }

    /// <summary>The outbox schema for this server.</summary>
    protected abstract IMigrationSource Migrations { get; }

    /// <summary>The migration runner's dialect for this server.</summary>
    protected abstract IMigrationDialect MigrationDialect { get; }

    /// <summary>Creates an empty database on the class's container.</summary>
    protected abstract Task<string> CreateDatabaseAsync();

    /// <summary>Creates an unopened provider connection.</summary>
    protected abstract DbConnection CreateConnection(string connectionString);

    /// <summary>Drops pooled connections, so tests do not pile up server sessions.</summary>
    protected abstract void ClearPools();

    public override async Task InitializeAsync()
    {
        _connectionString = await CreateDatabaseAsync().ConfigureAwait(false);
        var connection = await ConnectAsync().ConfigureAwait(false);
        await new MigrationRunner(connection, Migrations, MigrationDialect).RunAsync().ConfigureAwait(false);
    }

    public override async Task DisposeAsync()
    {
        for (var i = 0; i < _connections.Count; i++)
            await _connections[i].DisposeAsync().ConfigureAwait(false);
        ClearPools();
    }

    [Fact]
    public async Task Migration_2_Upgrades_A_Version_1_Database_Without_Losing_Existing_Rows()
    {
        // A database of its own, brought up to schema version 1 only, with a row written under
        // it before the lease columns exist.
        var connectionString = await CreateDatabaseAsync();
        var first = await ConnectAsync(connectionString);
        await new MigrationRunner(first, new FirstMigrationOnlySource(Migrations), MigrationDialect).RunAsync();
        await new OrmOutboxStore(first, Dialect)
            .EnqueueAsync("Pre.Existing", new byte[] { 1, 2, 3 }, transaction: null, CancellationToken.None);

        // Upgrade to the full schema, which applies migration 2 on top.
        var upgrade = await ConnectAsync(connectionString);
        await new MigrationRunner(upgrade, Migrations, MigrationDialect).RunAsync();

        // The pre-existing row is unleased and claimable under the new schema.
        var lease = new OutboxLease("upgraded-host", TimeSpan.FromMinutes(1));
        var claimed = await new OrmOutboxStore(await ConnectAsync(connectionString), Dialect)
            .ClaimPendingAsync(10, lease, CancellationToken.None);
        claimed.Should().ContainSingle().Which.TypeName.Should().Be("Pre.Existing");
    }

    [Fact]
    public async Task The_Outbox_And_An_Application_Source_Both_Numbered_From_1_Share_One_Database()
    {
        // Each source numbers its versions on its own in the scoped history table, so the
        // application's version 1 and the outbox's version 1 are both applied, once.
        var connectionString = await CreateDatabaseAsync();
        for (var run = 0; run < 2; run++)
        {
            await new MigrationRunner(await ConnectAsync(connectionString), new AppMigrationSource(), MigrationDialect)
                .RunAsync();
            await new MigrationRunner(await ConnectAsync(connectionString), Migrations, MigrationDialect)
                .RunAsync();
        }

        var history = new List<(string Source, long Version, string Name)>();
        var raw = CreateConnection(connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync();
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT source, version, name FROM __zaorm_migrations";
                var reader = await cmd.ExecuteReaderAsync();
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync())
                    {
                        history.Add((
                            reader.GetString(0),
                            Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                            reader.GetString(2)));
                    }
                }
            }
        }

        history.Should().BeEquivalentTo(
        [
            (AppMigrationSource.SourceName, 1L, "create_orders"),
            (AppMigrationSource.SourceName, 2L, "create_customers"),
            ("ZeroAlloc.Outbox.Orm", 1L, "create_outbox_messages"),
            ("ZeroAlloc.Outbox.Orm", 2L, "add_outbox_lease"),
        ]);
    }

    protected override async Task<IOutboxStore> StoreAsync()
        => new OrmOutboxStore(await ConnectAsync().ConfigureAwait(false), Dialect);

    protected override async Task<long> CountWithStatusAsync(int status)
    {
        var raw = CreateConnection(_connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM OutboxMessages WHERE Status = @status";
                AddParameter(cmd, "@status", status);
                var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    protected override async Task<StoredRow> ReadRowAsync(OutboxMessageId id)
    {
        var raw = CreateConnection(_connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT Status, RetryCount, LockedBy, LockedUntil FROM OutboxMessages WHERE Id = @id";
                AddParameter(cmd, "@id", id.Value);
                var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    (await reader.ReadAsync().ConfigureAwait(false)).Should().BeTrue("the message exists");
                    return new StoredRow(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
                }
            }
        }
    }

    private Task<IAsyncDbConnection> ConnectAsync() => ConnectAsync(_connectionString);

    /// <summary>An open connection, disposed with the test.</summary>
    private async Task<IAsyncDbConnection> ConnectAsync(string connectionString)
    {
        var connection = CreateConnection(connectionString).AsAsync();
        _connections.Add(connection);
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}
