using System.Data.Async;
using System.Data.Async.Adapters;
using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// <see cref="SqlServerOutboxRepository"/> on its own, over a real SQL Server table: the SQL its
/// placeholders produce, the guards on each mark, the locking claim, and the table and column name
/// handling.
/// </summary>
public sealed class SqlServerOutboxRepositoryTests(SqlServerFixture server) : IClassFixture<SqlServerFixture>
{
    private const int Pending = 0;
    private const int Succeeded = 1;
    private const int DeadLetter = 2;
    private const string Host = "host-a";

    private static readonly byte[] s_payload = [1, 2, 3];
    private static readonly TimeSpan s_leaseDuration = TimeSpan.FromMinutes(10);
    private static readonly DateTimeOffset s_now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Insert_ThenReadState_RoundTrips()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = Guid.NewGuid();

        (await InsertAsync(repo, id)).Should().Be(1);

        var state = await repo.ReadStateAsync(id, CancellationToken.None);
        state.Should().Be(new OutboxRowState(Pending, 0));
        (await repo.ReadStateAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task InsertInTransaction_EnlistsInTheCallersTransaction()
    {
        await using var fx = await MigratedAsync();
        var connection = await fx.ConnectAsync();
        var repo = new SqlServerOutboxRepository(connection, "OutboxMessages", quoteColumns: false);

        var tx = await connection.BeginTransactionAsync();
        await using (tx.ConfigureAwait(false))
        {
            (await repo.InsertInTransactionAsync(
                Guid.NewGuid(), "Test.Cmd", s_payload, Pending, s_now, s_now, tx, CancellationToken.None))
                .Should().Be(1);
            await tx.RollbackAsync();
        }

        (await ScalarAsync(fx, "SELECT COUNT(*) FROM OutboxMessages")).Should().Be(0);
    }

    [Fact]
    public async Task MarkProcessed_RequiresPendingStatusAndLease()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);

        (await repo.MarkProcessedAsync(id, Succeeded, s_now, Pending, "other-host", CancellationToken.None))
            .Should().Be(0);
        (await repo.ReadStateAsync(id, CancellationToken.None))!.Status.Should().Be(Pending);

        (await repo.MarkProcessedAsync(id, Succeeded, s_now, Pending, Host, CancellationToken.None))
            .Should().Be(1);
        (await repo.ReadStateAsync(id, CancellationToken.None))!.Status.Should().Be(Succeeded);
        (await ScalarAsync(fx, "SELECT LockedBy FROM OutboxMessages")).Should().Be(DBNull.Value);

        // No longer pending, so a second mark changes nothing.
        (await repo.MarkProcessedAsync(id, Succeeded, s_now, Pending, Host, CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task MarkForRetry_RecordsTheAttemptAndReleasesTheLease()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);

        (await repo.MarkForRetryAsync(id, 3, s_now.AddMinutes(5), Pending, "other-host", CancellationToken.None))
            .Should().Be(0);
        (await repo.MarkForRetryAsync(id, 3, s_now.AddMinutes(5), Pending, Host, CancellationToken.None))
            .Should().Be(1);

        (await repo.ReadStateAsync(id, CancellationToken.None)).Should().Be(new OutboxRowState(Pending, 3));
        (await ScalarAsync(fx, "SELECT LockedBy FROM OutboxMessages")).Should().Be(DBNull.Value);
    }

    [Fact]
    public async Task DeadLetter_StoresTheErrorAndGuardsOnTheLease()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);

        (await repo.DeadLetterAsync(id, DeadLetter, "boom", s_now, Pending, "other-host", CancellationToken.None))
            .Should().Be(0);
        (await repo.DeadLetterAsync(id, DeadLetter, "boom", s_now, Pending, Host, CancellationToken.None))
            .Should().Be(1);

        (await repo.ReadStateAsync(id, CancellationToken.None))!.Status.Should().Be(DeadLetter);
        (await ScalarAsync(fx, "SELECT DeadLetterError FROM OutboxMessages")).Should().Be("boom");
    }

    [Fact]
    public async Task ReleaseLease_OnlyForTheHolderOfAPendingRow()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);

        (await repo.ReleaseLeaseAsync(id, "other-host", Pending, CancellationToken.None)).Should().Be(0);
        (await ScalarAsync(fx, "SELECT LockedBy FROM OutboxMessages")).Should().Be(Host);

        (await repo.ReleaseLeaseAsync(id, Host, Pending, CancellationToken.None)).Should().Be(1);
        (await ScalarAsync(fx, "SELECT LockedBy FROM OutboxMessages")).Should().Be(DBNull.Value);
        (await ScalarAsync(fx, "SELECT LockedUntil FROM OutboxMessages")).Should().Be(DBNull.Value);
    }

    [Fact]
    public async Task RenewLease_RenewsAHeldLeaseButNotAnExpiredOne()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);
        var renewedUntil = s_now + (2 * s_leaseDuration);

        (await repo.RenewLeaseAsync(id, "other-host", Pending, s_now, renewedUntil, CancellationToken.None))
            .Should().Be(0);
        (await repo.RenewLeaseAsync(id, Host, Pending, s_now, renewedUntil, CancellationToken.None)).Should().Be(1);
        (await ScalarAsync(fx, "SELECT COUNT(*) FROM OutboxMessages WHERE LockedUntil IS NOT NULL"))
            .Should().Be(1);

        // The renewed lease ran out at renewedUntil; renewing after that is refused.
        var afterExpiry = renewedUntil.AddMinutes(1);
        (await repo.RenewLeaseAsync(id, Host, Pending, afterExpiry, afterExpiry + s_leaseDuration, CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task Requeue_ResetsADeadLetter()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);
        await repo.MarkForRetryAsync(id, 4, s_now, Pending, Host, CancellationToken.None);
        await repo.ClaimPendingAsync(Pending, s_now, s_now.AddMinutes(1), Host, 10, CancellationToken.None);
        await repo.DeadLetterAsync(id, DeadLetter, "boom", s_now, Pending, Host, CancellationToken.None);

        (await repo.RequeueAsync(id, Pending, s_now, DeadLetter, CancellationToken.None)).Should().Be(1);

        (await repo.ReadStateAsync(id, CancellationToken.None)).Should().Be(new OutboxRowState(Pending, 0));
        (await ScalarAsync(fx, "SELECT DeadLetterError FROM OutboxMessages")).Should().Be(DBNull.Value);
        (await ScalarAsync(fx, "SELECT ProcessedAt FROM OutboxMessages")).Should().Be(DBNull.Value);
        (await ScalarAsync(fx, "SELECT LockedBy FROM OutboxMessages")).Should().Be(DBNull.Value);
    }

    [Fact]
    public async Task Requeue_IgnoresANonDeadLetter()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);
        await repo.MarkForRetryAsync(id, 2, s_now, Pending, Host, CancellationToken.None);

        (await repo.RequeueAsync(id, Pending, s_now, DeadLetter, CancellationToken.None)).Should().Be(0);
        (await repo.RequeueAsync(Guid.NewGuid(), Pending, s_now, DeadLetter, CancellationToken.None)).Should().Be(0);

        (await repo.ReadStateAsync(id, CancellationToken.None)).Should().Be(new OutboxRowState(Pending, 2));
    }

    [Fact]
    public async Task ClaimPending_LeasesAndReturnsExactlyThoseRows()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < ids.Length; i++)
        {
            await repo.InsertAsync(
                ids[i], $"Cmd.{i}", s_payload, Pending, s_now, s_now.AddSeconds(i), CancellationToken.None);
        }

        var claimed = await repo.ClaimPendingAsync(
            Pending, s_now, s_now.AddMinutes(1), Host, 2, CancellationToken.None);

        claimed.Should().HaveCount(2);
        claimed.Select(r => r.Id).Should().BeSubsetOf(ids);
        claimed.Should().OnlyContain(r => r.Payload.SequenceEqual(s_payload));
        (await ScalarAsync(fx, "SELECT COUNT(*) FROM OutboxMessages WHERE LockedBy = 'host-a'"))
            .Should().Be(2);

        // Only the unleased third row is left to claim.
        var rest = await repo.ClaimPendingAsync(
            Pending, s_now, s_now.AddMinutes(1), "host-b", 10, CancellationToken.None);
        rest.Should().ContainSingle();
        rest.Select(r => r.Id).Should().NotIntersectWith(claimed.Select(r => r.Id));
    }

    [Fact]
    public async Task CustomTable_UsesTheConfiguredName()
    {
        await using var fx = await CreateAsync();
        await CreateTableAsync(fx, "[Custom Outbox]", quoteColumns: false);
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "[Custom Outbox]", quoteColumns: false);
        var id = Guid.NewGuid();

        await InsertAsync(repo, id);
        var claimed = await repo.ClaimPendingAsync(
            Pending, s_now, s_now.AddMinutes(1), Host, 10, CancellationToken.None);

        claimed.Should().ContainSingle().Which.Id.Should().Be(id);
        (await ScalarAsync(fx, "SELECT COUNT(*) FROM [Custom Outbox]")).Should().Be(1);
    }

    [Fact]
    public async Task QuotedColumns_RoundTrip()
    {
        await using var fx = await CreateAsync();
        await CreateTableAsync(fx, "Quoted", quoteColumns: true);
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "Quoted", quoteColumns: true);
        var id = Guid.NewGuid();

        await InsertAsync(repo, id);
        var claimed = await repo.ClaimPendingAsync(
            Pending, s_now, s_now.AddMinutes(1), Host, 10, CancellationToken.None);
        claimed.Should().ContainSingle();
        (await repo.RenewLeaseAsync(id, Host, Pending, s_now, s_now.AddMinutes(2), CancellationToken.None))
            .Should().Be(1);
        (await repo.MarkForRetryAsync(id, 1, s_now, Pending, Host, CancellationToken.None)).Should().Be(1);
        (await repo.DeadLetterAsync(id, DeadLetter, "e", s_now, Pending, "none", CancellationToken.None))
            .Should().Be(0);
        (await repo.ReadStateAsync(id, CancellationToken.None)).Should().Be(new OutboxRowState(Pending, 1));
    }

    [Fact]
    public async Task UnquotedColumns_RoundTripOnAnUnquotedTable()
    {
        await using var fx = await CreateAsync();
        await CreateTableAsync(fx, "Plain", quoteColumns: false);
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "Plain", quoteColumns: false);
        var id = Guid.NewGuid();

        await InsertAsync(repo, id);
        var claimed = await repo.ClaimPendingAsync(
            Pending, s_now, s_now.AddMinutes(1), Host, 10, CancellationToken.None);
        claimed.Should().ContainSingle();
        (await repo.MarkProcessedAsync(id, Succeeded, s_now, Pending, Host, CancellationToken.None)).Should().Be(1);
        (await repo.ReadStateAsync(id, CancellationToken.None)).Should().Be(new OutboxRowState(Succeeded, 0));
    }

    [Fact]
    public async Task ClaimPending_SkipsARowWhoseLeaseIsStillLive()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);

        var whileLive = await repo.ClaimPendingAsync(
            Pending, s_now.AddMinutes(1), s_now.AddMinutes(2), "host-b", 10, CancellationToken.None);
        whileLive.Should().BeEmpty();
        (await ScalarAsync(fx, "SELECT LockedBy FROM OutboxMessages")).Should().Be(Host);

        var afterExpiry = s_now + s_leaseDuration + TimeSpan.FromMinutes(1);
        var takenOver = await repo.ClaimPendingAsync(
            Pending, afterExpiry, afterExpiry + s_leaseDuration, "host-b", 10, CancellationToken.None);
        takenOver.Should().ContainSingle().Which.Id.Should().Be(id);
        (await ScalarAsync(fx, "SELECT LockedBy FROM OutboxMessages")).Should().Be("host-b");
    }

    [Fact]
    public async Task EveryStatement_QuotesColumnsOnlyWhenAsked()
    {
        string[] columns =
        [
            "Id", "TypeName", "Payload", "Status", "RetryCount", "NextRetryAt", "CreatedAt",
            "ProcessedAt", "DeadLetterError", "LockedBy", "LockedUntil",
        ];
        var bareColumn = new Regex(@"(?<!\[|\w)(" + string.Join('|', columns) + @")(?!\]|\w)", RegexOptions.None, TimeSpan.FromSeconds(5));

        var plain = await RunEveryStatementAsync(quoteColumns: false);
        var quoted = await RunEveryStatementAsync(quoteColumns: true);

        plain.Should().HaveCount(10);
        quoted.Should().HaveCount(plain.Count);
        for (var i = 0; i < plain.Count; i++)
        {
            plain[i].Should().NotContain("[", $"statement {i} is unquoted when quoteColumns is false");
            quoted[i].Should().Contain("[Id]", $"statement {i} quotes its columns");
            bareColumn.IsMatch(quoted[i]).Should().BeFalse($"statement {i} leaves no column bare: {quoted[i]}");
            quoted[i].Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal).Should().Be(
                plain[i], $"statement {i} differs only by the quotes");
        }
    }

    [Fact]
    public async Task ClaimPending_SkipsRowsLockedByAnotherClaimer()
    {
        await using var fx = await MigratedAsync();
        var seed = new SqlServerOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(Guid.NewGuid());
            await seed.InsertAsync(
                ids[i], $"Cmd.{i}", s_payload, Pending, s_now, s_now.AddSeconds(i), CancellationToken.None);
        }

        // A second claimer holds row locks on the two oldest rows inside a transaction it keeps open.
        // Without READPAST the repository's claim would wait for that transaction; with it the claim
        // passes over the locked rows and leases the other two.
        var raw = new SqlConnection(fx.ConnectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync();
            var holder = await raw.BeginTransactionAsync();
            await using (holder.ConfigureAwait(false))
            {
                var hold = raw.CreateCommand();
                await using (hold.ConfigureAwait(false))
                {
                    hold.Transaction = (SqlTransaction)holder;
                    // By primary key, so only those two rows are locked and not every row a scan reads.
                    hold.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
                        UPDATE OutboxMessages WITH (ROWLOCK) SET LockedBy = 'holder'
                        WHERE Id IN ('{ids[0]}', '{ids[1]}')
                        """);
                    (await hold.ExecuteNonQueryAsync()).Should().Be(2);
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var claimed = await seed.ClaimPendingAsync(
                    Pending, s_now.AddMinutes(1), s_now.AddMinutes(1) + s_leaseDuration, Host, 2, timeout.Token);

                claimed.Select(r => r.Id).Should().BeEquivalentTo(ids.Skip(2), "the two oldest rows are locked");
                await holder.RollbackAsync();
            }
        }

        (await ScalarAsync(fx, "SELECT COUNT(*) FROM OutboxMessages WHERE LockedBy = 'host-a'")).Should().Be(2);
        (await ScalarAsync(fx, "SELECT COUNT(*) FROM OutboxMessages WHERE LockedBy IS NULL")).Should().Be(2);
    }

    /// <summary>Runs all ten statements on a fresh table and returns the SQL text each one sent.</summary>
    internal async Task<IReadOnlyList<string>> RunEveryStatementAsync(bool quoteColumns)
    {
        var fx = await CreateAsync().ConfigureAwait(false);
        await using var _ = fx.ConfigureAwait(false);
        await CreateTableAsync(fx, "Outbox", quoteColumns).ConfigureAwait(false);
        var recording = new RecordingAsyncConnection(await fx.ConnectAsync().ConfigureAwait(false));
        var repo = new SqlServerOutboxRepository(recording, "Outbox", quoteColumns);
        var id = Guid.NewGuid();
        var ct = CancellationToken.None;

        await repo.InsertAsync(id, "Test.Cmd", s_payload, Pending, s_now, s_now, ct).ConfigureAwait(false);
        var tx = await recording.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            await repo.InsertInTransactionAsync(
                Guid.NewGuid(), "Test.Cmd", s_payload, Pending, s_now, s_now, tx, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await repo.ClaimPendingAsync(Pending, s_now, s_now + s_leaseDuration, Host, 10, ct).ConfigureAwait(false);
        await repo.RenewLeaseAsync(id, Host, Pending, s_now, s_now + s_leaseDuration, ct).ConfigureAwait(false);
        await repo.MarkForRetryAsync(id, 1, s_now, Pending, Host, ct).ConfigureAwait(false);
        await repo.ReleaseLeaseAsync(id, Host, Pending, ct).ConfigureAwait(false);
        await repo.MarkProcessedAsync(id, Succeeded, s_now, Pending, Host, ct).ConfigureAwait(false);
        await repo.DeadLetterAsync(id, DeadLetter, "e", s_now, Pending, Host, ct).ConfigureAwait(false);
        await repo.RequeueAsync(id, Pending, s_now, DeadLetter, ct).ConfigureAwait(false);
        await repo.ReadStateAsync(id, ct).ConfigureAwait(false);

        return recording.Commands.ToList();
    }

    private async Task<SqlServerDb> CreateAsync()
        => new(await server.CreateDatabaseAsync().ConfigureAwait(false), server);

    private async Task<SqlServerDb> MigratedAsync()
    {
        var db = await CreateAsync().ConfigureAwait(false);
        var connection = await db.ConnectAsync().ConfigureAwait(false);
        await new MigrationRunner(connection, OutboxOrmMigrations.SqlServer, new SqlServerMigrationDialect())
            .RunAsync().ConfigureAwait(false);
        return db;
    }

    private static Task<int> InsertAsync(SqlServerOutboxRepository repo, Guid id)
        => repo.InsertAsync(id, "Test.Cmd", s_payload, Pending, s_now, s_now, CancellationToken.None);

    /// <summary>
    /// Inserts one row and leases it to <see cref="Host"/> from <c>s_now</c> for <c>s_leaseDuration</c>.
    /// </summary>
    private static async Task<Guid> InsertLeasedAsync(SqlServerOutboxRepository repo)
    {
        var id = Guid.NewGuid();
        await InsertAsync(repo, id).ConfigureAwait(false);
        var claimed = await repo.ClaimPendingAsync(
            Pending, s_now, s_now + s_leaseDuration, Host, 10, CancellationToken.None).ConfigureAwait(false);
        claimed.Should().ContainSingle();
        return id;
    }

    private static async Task<object?> ScalarAsync(SqlServerDb fx, string sql)
    {
        var raw = new SqlConnection(fx.ConnectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Creates the outbox table. With <paramref name="quoteColumns"/> the columns are quoted and keep
    /// their mixed case, as EF Core creates them. SQL Server resolves column names case-insensitively, so
    /// a successful round trip does not by itself prove the quoting; the recording test does.
    /// </summary>
    private static async Task CreateTableAsync(SqlServerDb fx, string table, bool quoteColumns)
    {
        string C(string name) => quoteColumns ? "[" + name + "]" : name;
        var raw = new SqlConnection(fx.ConnectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
                    CREATE TABLE {table} (
                        {C("Id")} UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                        {C("TypeName")} NVARCHAR(256) NOT NULL,
                        {C("Payload")} VARBINARY(MAX) NOT NULL,
                        {C("Status")} INT NOT NULL,
                        {C("RetryCount")} INT NOT NULL,
                        {C("NextRetryAt")} DATETIMEOFFSET NOT NULL,
                        {C("CreatedAt")} DATETIMEOFFSET NOT NULL,
                        {C("ProcessedAt")} DATETIMEOFFSET NULL,
                        {C("DeadLetterError")} NVARCHAR(MAX) NULL,
                        {C("LockedBy")} NVARCHAR(128) NULL,
                        {C("LockedUntil")} DATETIMEOFFSET NULL
                    )
                    """);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>A database of its own on the class's container.</summary>
    private sealed class SqlServerDb(string connectionString, SqlServerFixture server) : IAsyncDisposable
    {
        private readonly List<IAsyncDbConnection> _connections = [];

        public string ConnectionString { get; } = connectionString;

        public async Task<IAsyncDbConnection> ConnectAsync()
        {
            var raw = new SqlConnection(ConnectionString);
            var connection = raw.AsAsync();
            await connection.OpenAsync().ConfigureAwait(false);
            _connections.Add(connection);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections)
                await connection.DisposeAsync().ConfigureAwait(false);
            server.ClearPools();
        }
    }
}
