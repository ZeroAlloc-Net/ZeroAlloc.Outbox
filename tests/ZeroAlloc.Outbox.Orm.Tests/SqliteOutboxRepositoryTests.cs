using System.Data.Async;
using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// <see cref="SqliteOutboxRepository"/> on its own, over a real SQLite table: the SQL its
/// placeholders produce, the guards on each mark, and the table and column name handling.
/// </summary>
public sealed class SqliteOutboxRepositoryTests
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(connection, "OutboxMessages", quoteColumns: false);

        var tx = await connection.BeginTransactionAsync();
        await using (tx.ConfigureAwait(false))
        {
            (await repo.InsertInTransactionAsync(
                Guid.NewGuid(), "Test.Cmd", s_payload, Pending, s_now, s_now, tx, CancellationToken.None))
                .Should().Be(1);
            await tx.RollbackAsync();
        }

        (await fx.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MarkProcessed_RequiresPendingStatusAndLease()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
        var id = await InsertLeasedAsync(repo);
        var renewedUntil = s_now + (2 * s_leaseDuration);

        (await repo.RenewLeaseAsync(id, "other-host", Pending, s_now, renewedUntil, CancellationToken.None))
            .Should().Be(0);
        (await repo.RenewLeaseAsync(id, Host, Pending, s_now, renewedUntil, CancellationToken.None)).Should().Be(1);
        (await ScalarAsync(fx, "SELECT COUNT(*) FROM OutboxMessages WHERE LockedUntil IS NOT NULL"))
            .Should().Be(1L);

        // The renewed lease ran out at renewedUntil; renewing after that is refused.
        var afterExpiry = renewedUntil.AddMinutes(1);
        (await repo.RenewLeaseAsync(id, Host, Pending, afterExpiry, afterExpiry + s_leaseDuration, CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task Requeue_ResetsADeadLetter()
    {
        await using var fx = await MigratedAsync();
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
            .Should().Be(2L);

        // Only the unleased third row is left to claim.
        var rest = await repo.ClaimPendingAsync(
            Pending, s_now, s_now.AddMinutes(1), "host-b", 10, CancellationToken.None);
        rest.Should().ContainSingle();
        rest.Select(r => r.Id).Should().NotIntersectWith(claimed.Select(r => r.Id));
    }

    [Fact]
    public async Task CustomTable_UsesTheConfiguredName()
    {
        await using var fx = new SqliteFixture();
        await CreateTableAsync(fx, "\"Custom Outbox\"", quoteColumns: false);
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "\"Custom Outbox\"", quoteColumns: false);
        var id = Guid.NewGuid();

        await InsertAsync(repo, id);
        var claimed = await repo.ClaimPendingAsync(
            Pending, s_now, s_now.AddMinutes(1), Host, 10, CancellationToken.None);

        claimed.Should().ContainSingle().Which.Id.Should().Be(id);
        (await ScalarAsync(fx, "SELECT COUNT(*) FROM \"Custom Outbox\"")).Should().Be(1L);
    }

    [Fact]
    public async Task QuotedColumns_RoundTrip()
    {
        await using var fx = new SqliteFixture();
        await CreateTableAsync(fx, "Quoted", quoteColumns: true);
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "Quoted", quoteColumns: true);
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
        await using var fx = new SqliteFixture();
        await CreateTableAsync(fx, "Plain", quoteColumns: false);
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "Plain", quoteColumns: false);
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
        var repo = new SqliteOutboxRepository(await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);
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
        var bareColumn = new Regex("(?<!\"|\\w)(" + string.Join('|', columns) + ")(?!\"|\\w)", RegexOptions.None, TimeSpan.FromSeconds(5));

        var plain = await RunEveryStatementAsync(quoteColumns: false);
        var quoted = await RunEveryStatementAsync(quoteColumns: true);

        plain.Should().HaveCount(10);
        quoted.Should().HaveCount(plain.Count);
        for (var i = 0; i < plain.Count; i++)
        {
            plain[i].Should().NotContain("\"", $"statement {i} is unquoted when quoteColumns is false");
            quoted[i].Should().Contain("\"Id\"", $"statement {i} quotes its columns");
            bareColumn.IsMatch(quoted[i]).Should().BeFalse($"statement {i} leaves no column bare: {quoted[i]}");
            quoted[i].Replace("\"", string.Empty, StringComparison.Ordinal).Should().Be(
                plain[i], $"statement {i} differs only by the quotes");
        }
    }

    /// <summary>Runs all ten statements on a fresh table and returns the SQL text each one sent.</summary>
    internal static async Task<IReadOnlyList<string>> RunEveryStatementAsync(bool quoteColumns)
    {
        var fx = new SqliteFixture();
        await using var _ = fx.ConfigureAwait(false);
        await CreateTableAsync(fx, "Outbox", quoteColumns).ConfigureAwait(false);
        var recording = new RecordingAsyncConnection(await fx.ConnectAsync().ConfigureAwait(false));
        var repo = new SqliteOutboxRepository(recording, "Outbox", quoteColumns);
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

    private static async Task<SqliteFixture> MigratedAsync()
    {
        var fx = new SqliteFixture();
        await fx.MigrateAsync().ConfigureAwait(false);
        return fx;
    }

    private static Task<int> InsertAsync(SqliteOutboxRepository repo, Guid id)
        => repo.InsertAsync(id, "Test.Cmd", s_payload, Pending, s_now, s_now, CancellationToken.None);

    /// <summary>
    /// Inserts one row and leases it to <see cref="Host"/> from <c>s_now</c> for <c>s_leaseDuration</c>.
    /// </summary>
    private static async Task<Guid> InsertLeasedAsync(SqliteOutboxRepository repo)
    {
        var id = Guid.NewGuid();
        await InsertAsync(repo, id).ConfigureAwait(false);
        var claimed = await repo.ClaimPendingAsync(
            Pending, s_now, s_now + s_leaseDuration, Host, 10, CancellationToken.None).ConfigureAwait(false);
        claimed.Should().ContainSingle();
        return id;
    }

    private static async Task<object?> ScalarAsync(SqliteFixture fx, string sql)
    {
        var raw = await fx.OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return value is long l ? l : value;
            }
        }
    }

    private static async Task CreateTableAsync(SqliteFixture fx, string table, bool quoteColumns)
    {
        string C(string name) => quoteColumns ? "\"" + name + "\"" : name;
        var raw = await fx.OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
                    CREATE TABLE {table} (
                        {C("Id")} BLOB NOT NULL PRIMARY KEY,
                        {C("TypeName")} TEXT NOT NULL,
                        {C("Payload")} BLOB NOT NULL,
                        {C("Status")} INTEGER NOT NULL,
                        {C("RetryCount")} INTEGER NOT NULL,
                        {C("NextRetryAt")} TEXT NOT NULL,
                        {C("CreatedAt")} TEXT NOT NULL,
                        {C("ProcessedAt")} TEXT NULL,
                        {C("DeadLetterError")} TEXT NULL,
                        {C("LockedBy")} TEXT NULL,
                        {C("LockedUntil")} TEXT NULL
                    )
                    """);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }
}
