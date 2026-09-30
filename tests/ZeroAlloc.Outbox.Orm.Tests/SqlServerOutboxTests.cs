using System.Data.Async;
using System.Data.Async.Adapters;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The store against a real SQL Server, which is the only way to prove the
/// SQL Server claim, an updatable TOP CTE with READPAST and OUTPUT, and the
/// DDL actually work. One container serves the class, and each test gets a database of its own.
/// </summary>
public sealed class SqlServerOutboxTests(SqlServerFixture server) : IAsyncLifetime, IClassFixture<SqlServerFixture>
{
    private static readonly OutboxLease s_lease = new("test-host", TimeSpan.FromMinutes(1));
    private static readonly byte[] s_payload = [1, 2, 3];

    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _connectionString = await server.CreateDatabaseAsync().ConfigureAwait(false);
        var conn = await ConnectAsync().ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await new MigrationRunner(conn, OutboxOrmMigrations.SqlServer, new SqlServerMigrationDialect())
                .RunAsync(default).ConfigureAwait(false);
        }
    }

    public Task DisposeAsync()
    {
        server.ClearPools();
        return Task.CompletedTask;
    }

    private async Task<IAsyncDbConnection> ConnectAsync()
        => await ConnectAsync(_connectionString).ConfigureAwait(false);

    private static async Task<IAsyncDbConnection> ConnectAsync(string connectionString)
    {
        var raw = new SqlConnection(connectionString);
        var c = raw.AsAsync();
        await c.OpenAsync().ConfigureAwait(false);
        return c;
    }

    private async Task<OrmOutboxStore> StoreAsync()
        => new(await ConnectAsync().ConfigureAwait(false), OutboxOrmDialect.SqlServer);

    [Fact]
    public async Task Enqueue_Then_Claim_Round_Trips_On_SqlServer()
    {
        var store = await StoreAsync();

        await store.EnqueueAsync("Sql.Cmd", s_payload, transaction: null, CancellationToken.None);
        var pending = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);

        pending.Should().ContainSingle();
        pending[0].TypeName.Should().Be("Sql.Cmd");
        pending[0].RawPayload.Should().Equal(s_payload);
    }

    [Fact]
    public async Task Claim_Honours_BatchSize_And_Returns_The_Oldest_First()
    {
        // SQL Server has neither LIMIT nor RETURNING, so the claim bounds the batch
        // with TOP inside an ordered CTE and returns the leased rows through OUTPUT.
        var store = await StoreAsync();
        for (var i = 0; i < 7; i++)
            await store.EnqueueAsync($"Batch.{i}", s_payload, null, CancellationToken.None);

        var batch = await store.ClaimPendingAsync(3, s_lease, CancellationToken.None);

        batch.Should().HaveCount(3);
        batch.Should().BeInAscendingOrder(e => e.CreatedAt);
    }

    [Fact]
    public async Task Transitions_Work_On_SqlServer()
    {
        var store = await StoreAsync();
        await store.EnqueueAsync("Flow", s_payload, null, CancellationToken.None);
        var entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None))[0];

        // Each mark releases the lease, so every later mark follows a fresh claim.
        (await store.MarkFailedAsync(entry.Id, 1, DateTimeOffset.UtcNow.AddSeconds(-1), s_lease, CancellationToken.None))
            .Should().BeTrue();
        var again = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);
        again.Should().ContainSingle();
        again[0].RetryCount.Should().Be(1);

        (await store.MarkFailedAsync(entry.Id, 2, DateTimeOffset.UtcNow.AddMinutes(5), s_lease, CancellationToken.None))
            .Should().BeTrue();
        (await store.ClaimPendingAsync(10, s_lease, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Leases_Marks_And_Release_Work_On_SqlServer()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        for (var i = 0; i < 3; i++)
            await a.EnqueueAsync($"Lease.{i}", s_payload, null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMinutes(10));
        var byA = await a.ClaimPendingAsync(3, leaseA, CancellationToken.None);
        byA.Should().HaveCount(3);

        // Leased rows are invisible to another host, and only the holder can renew.
        (await b.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().BeEmpty();
        (await a.RenewLeaseAsync(byA[0].Id, leaseA, CancellationToken.None)).Should().BeTrue();
        (await b.RenewLeaseAsync(byA[0].Id, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().BeFalse();

        // Marks are conditional: only the lease holder can mark, the first mark moves the row,
        // and a second one reports false.
        (await b.MarkSucceededAsync(byA[0].Id, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().BeFalse();
        (await a.MarkSucceededAsync(byA[0].Id, leaseA, CancellationToken.None)).Should().BeTrue();
        (await a.MarkFailedAsync(byA[0].Id, 1, DateTimeOffset.UtcNow, leaseA, CancellationToken.None)).Should().BeFalse();

        // Releasing hands the pending rows back at once; the dispatched one stays put.
        (await a.ReleaseLeasesAsync([byA[0].Id, byA[1].Id, byA[2].Id], leaseA, CancellationToken.None))
            .Should().Be(2, "only this host's pending rows are released");
        var byB = await b.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None);
        byB.Select(e => e.Id).Should().BeEquivalentTo([byA[1].Id, byA[2].Id]);
    }

    [Fact]
    public async Task Enqueue_In_A_Transaction_Rolls_Back_On_SqlServer()
    {
        // Enlisting on the caller's connection has to work per-provider, not just
        // on SQLite where it was first written.
        var store = await StoreAsync();
        var raw = new SqlConnection(_connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync();
            var tx = (System.Data.Common.DbTransaction)await raw.BeginTransactionAsync();
            await store.EnqueueAsync("Rolled.Back", s_payload, tx, CancellationToken.None);
            await tx.RollbackAsync();
        }

        (await store.ClaimPendingAsync(10, s_lease, CancellationToken.None))
            .Should().NotContain(e => e.TypeName == "Rolled.Back");
    }

    [Fact]
    public async Task EnqueueInTransaction_Commits_And_Rolls_Back_With_The_Orm_Transaction_On_SqlServer()
    {
        // SqlClient rejects a command whose transaction belongs to another connection, so this
        // proves the insert runs on the ORM transaction's own connection.
        var store = await StoreAsync();
        var caller = await ConnectAsync();
        await using (caller.ConfigureAwait(false))
        {
            var rolledBack = await caller.BeginTransactionAsync();
            await using (rolledBack.ConfigureAwait(false))
            {
                await store.EnqueueInTransactionAsync("Rolled.Back", s_payload, rolledBack, CancellationToken.None);
                await rolledBack.RollbackAsync();
            }

            var committed = await caller.BeginTransactionAsync();
            await using (committed.ConfigureAwait(false))
            {
                await store.EnqueueInTransactionAsync("Committed", s_payload, committed, CancellationToken.None);
                await committed.CommitAsync();
            }
        }

        var pending = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);
        pending.Should().ContainSingle().Which.TypeName.Should().Be("Committed");
    }

    [Fact]
    public async Task Migration_2_Adds_Lease_Columns_On_SqlServer_Without_Losing_Existing_Rows()
    {
        // A database of its own, separate from the one InitializeAsync already
        // fully migrated, so this test can start from a genuine v1 schema.
        var connectionString = await server.CreateDatabaseAsync();

        var first = await ConnectAsync(connectionString);
        await using (first.ConfigureAwait(false))
        {
            var source = new FirstMigrationOnlySource(OutboxOrmMigrations.SqlServer);
            await new MigrationRunner(first, source, new SqlServerMigrationDialect()).RunAsync();
        }

        var store = new OrmOutboxStore(await ConnectAsync(connectionString), OutboxOrmDialect.SqlServer);
        await store.EnqueueAsync("Pre.Existing", s_payload, transaction: null, CancellationToken.None);

        var upgrade = await ConnectAsync(connectionString);
        await using (upgrade.ConfigureAwait(false))
        {
            await new MigrationRunner(upgrade, OutboxOrmMigrations.SqlServer, new SqlServerMigrationDialect())
                .RunAsync();
        }

        await AssertLeaseColumnsAsync(connectionString);
    }

    [Fact]
    public async Task Clearing_This_Tests_Pools_Leaves_Another_Tests_Pool_Alone()
    {
        // xUnit runs test classes in parallel, and each clears its pools when a test ends. A
        // process-wide clear shuts down the pool another class is opening a connection on, and
        // SqlClient then fails that open with "Timeout expired ... obtaining a connection from
        // the pool". The other class's pool is simulated by a connection string the fixture
        // did not hand out: its own application name gives it a pool of its own.
        var other = new SqlConnectionStringBuilder(_connectionString) { ApplicationName = "another-test" }
            .ConnectionString;
        var before = await ServerProcessIdAsync(other);

        server.ClearPools();

        (await ServerProcessIdAsync(other)).Should().Be(before, "the other pool keeps its connection");
    }

    /// <summary>The server session a pooled connection runs on.</summary>
    private static async Task<short> ServerProcessIdAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT @@SPID";
                return (short)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
            }
        }
    }

    /// <summary>
    /// Confirms migration 2 landed: the lease columns exist, per
    /// <c>INFORMATION_SCHEMA.COLUMNS</c>, and the pre-existing row is unleased.
    /// </summary>
    private static async Task AssertLeaseColumnsAsync(string connectionString)
    {
        var verify = new SqlConnection(connectionString);
        await using (verify.ConfigureAwait(false))
        {
            await verify.OpenAsync().ConfigureAwait(false);

            var columnsCmd = verify.CreateCommand();
            columnsCmd.CommandText = """
                SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'OutboxMessages' AND COLUMN_NAME IN ('LockedBy', 'LockedUntil')
                """;
            var columns = new List<string>();
            var columnsReader = await columnsCmd.ExecuteReaderAsync().ConfigureAwait(false);
            await using (columnsReader.ConfigureAwait(false))
            {
                while (await columnsReader.ReadAsync().ConfigureAwait(false))
                {
                    columns.Add(columnsReader.GetString(0));
                }
            }

            columns.Should().BeEquivalentTo(["LockedBy", "LockedUntil"]);

            var rowCmd = verify.CreateCommand();
            rowCmd.CommandText =
                "SELECT LockedBy, LockedUntil FROM OutboxMessages WHERE TypeName = 'Pre.Existing'";
            var rowReader = await rowCmd.ExecuteReaderAsync().ConfigureAwait(false);
            await using (rowReader.ConfigureAwait(false))
            {
                (await rowReader.ReadAsync().ConfigureAwait(false)).Should().BeTrue();
                rowReader.IsDBNull(0).Should().BeTrue("a pre-existing row is unleased");
                rowReader.IsDBNull(1).Should().BeTrue("a pre-existing row is unleased");
            }
        }
    }
}
