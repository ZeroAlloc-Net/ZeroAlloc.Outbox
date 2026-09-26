using System.Collections.Concurrent;
using System.Data.Async;
using System.Data.Async.Adapters;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The lease-based claim on <see cref="OrmOutboxStore"/> against a SQLite file database,
/// with each claimer on its own connection, the way separate hosts would be.
/// </summary>
public sealed class OrmOutboxClaimTests : IAsyncLifetime
{
    private static readonly byte[] s_payload = [1, 2, 3];

    // The persisted values of OrmOutboxMessageStatus, which is internal to the store.
    private const long PendingStatus = 0;
    private const long SucceededStatus = 1;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"outbox-claim-{Guid.NewGuid():N}.db");
    private readonly List<IAsyncDbConnection> _connections = [];

    public async Task InitializeAsync()
    {
        var connection = await ConnectAsync().ConfigureAwait(false);
        await new MigrationRunner(connection, OutboxOrmMigrations.Sqlite, new SqliteMigrationDialect())
            .RunAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        for (var i = 0; i < _connections.Count; i++)
            await _connections[i].DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Concurrent_Claimers_Claim_Every_Message_Exactly_Once()
    {
        const int messages = 200;
        const int claimers = 8;
        var writer = await StoreAsync();
        for (var i = 0; i < messages; i++)
            await writer.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);

        var claimed = new ConcurrentBag<OutboxMessageId>();
        var tasks = new Task[claimers];
        for (var c = 0; c < claimers; c++)
        {
            var store = await StoreAsync();
            var lease = new OutboxLease($"h{c}", TimeSpan.FromMinutes(1));
            tasks[c] = Task.Run(async () =>
            {
                while (true)
                {
                    var batch = await store.ClaimPendingAsync(10, lease, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (batch.Count == 0) return;
                    foreach (var entry in batch)
                    {
                        claimed.Add(entry.Id);
                        await store.MarkSucceededAsync(entry.Id, lease, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        claimed.Should().HaveCount(messages);
        claimed.Distinct().Should().HaveCount(messages, "no message may be claimed twice");
    }

    [Fact]
    public async Task A_Claimed_Message_Is_Not_Claimable_By_Another_Host_While_Leased()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);

        (await a.ClaimPendingAsync(10, new OutboxLease("a", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().ContainSingle();
        (await b.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_Lost_Lease_Is_Reclaimed_And_Cannot_Be_Renewed_By_Its_Old_Host()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await a.ClaimPendingAsync(10, leaseA, CancellationToken.None))[0].Id;

        await Task.Delay(100);
        (await b.ClaimPendingAsync(10, leaseB, CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(id);

        (await a.RenewLeaseAsync(id, leaseA, CancellationToken.None)).Should().BeFalse();
        (await b.RenewLeaseAsync(id, leaseB, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Release_Semantics_Follow_The_Outcome()
    {
        var store = await StoreAsync();
        for (var i = 0; i < 3; i++)
            await store.EnqueueAsync($"T{i}", s_payload, transaction: null, CancellationToken.None);
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));
        var batch = await store.ClaimPendingAsync(10, lease, CancellationToken.None);

        await store.MarkFailedAsync(batch[0].Id, 1, DateTimeOffset.UtcNow, lease, CancellationToken.None);
        await store.MarkSucceededAsync(batch[1].Id, lease, CancellationToken.None);
        await store.DeadLetterAsync(batch[2].Id, "boom", lease, CancellationToken.None);

        var again = await store.ClaimPendingAsync(
            10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None);
        again.Should().ContainSingle().Which.Id.Should().Be(batch[0].Id);
    }

    [Fact]
    public async Task A_Late_Mark_After_A_Lost_Lease_Does_Not_Overwrite_The_New_Holders_Outcome()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await a.ClaimPendingAsync(10, leaseA, CancellationToken.None))[0].Id;

        await Task.Delay(100);
        (await b.ClaimPendingAsync(10, leaseB, CancellationToken.None)).Should().ContainSingle();
        (await b.MarkSucceededAsync(id, leaseB, CancellationToken.None)).Should().BeTrue();

        (await a.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, CancellationToken.None)).Should().BeFalse();

        (await ReadStatusAsync(id)).Should().Be(SucceededStatus, "Dispatched is terminal");
        (await ReadRetryCountAsync(id)).Should().Be(0, "the late mark must not touch the row at all");
    }

    [Fact]
    public async Task Released_Leases_Are_Claimable_At_Once_But_Only_This_Hosts_Pending_Ones()
    {
        var a = await StoreAsync();
        var c = await StoreAsync();
        var b = await StoreAsync();
        for (var i = 0; i < 3; i++)
            await a.EnqueueAsync($"T{i}", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMinutes(10));
        var byA = await a.ClaimPendingAsync(2, leaseA, CancellationToken.None);
        var byC = await c.ClaimPendingAsync(1, new OutboxLease("c", TimeSpan.FromMinutes(10)), CancellationToken.None);
        (await a.MarkSucceededAsync(byA[0].Id, leaseA, CancellationToken.None)).Should().BeTrue();

        (await a.ReleaseLeasesAsync([byA[0].Id, byA[1].Id, byC[0].Id], leaseA, CancellationToken.None))
            .Should().Be(1, "only this host's pending rows are released");

        var byB = await b.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None);
        byB.Should().ContainSingle().Which.Id.Should().Be(byA[1].Id);
    }

    [Fact]
    public async Task A_Host_Whose_Lease_Was_Taken_Over_Cannot_Mark_And_Leaves_The_New_Lease_Alone()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var id = (await a.ClaimPendingAsync(10, leaseA, CancellationToken.None))[0].Id;

        await Task.Delay(100);
        (await b.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().ContainSingle();
        var lockedUntilB = await ReadTextAsync(id, "LockedUntil");

        (await a.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, CancellationToken.None)).Should().BeFalse();
        (await a.MarkSucceededAsync(id, leaseA, CancellationToken.None)).Should().BeFalse();
        (await a.DeadLetterAsync(id, "late", leaseA, CancellationToken.None)).Should().BeFalse();

        (await ReadStatusAsync(id)).Should().Be(PendingStatus);
        (await ReadRetryCountAsync(id)).Should().Be(0);
        (await ReadTextAsync(id, "LockedBy")).Should().Be("b", "host b's lease must survive host a's late marks");
        (await ReadTextAsync(id, "LockedUntil")).Should().NotBeNull().And.Be(lockedUntilB);
    }

    [Fact]
    public async Task A_Host_Whose_Lease_Expired_Unclaimed_Can_Still_Mark()
    {
        var store = await StoreAsync();
        await store.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var lease = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var id = (await store.ClaimPendingAsync(10, lease, CancellationToken.None))[0].Id;

        await Task.Delay(100);

        (await store.MarkSucceededAsync(id, lease, CancellationToken.None)).Should().BeTrue();
        (await ReadStatusAsync(id)).Should().Be(SucceededStatus);
        (await ReadTextAsync(id, "LockedBy")).Should().BeNull();
        (await ReadTextAsync(id, "LockedUntil")).Should().BeNull();
    }

    [Fact]
    public async Task A_Retry_Time_With_A_Non_Utc_Offset_Is_Stored_As_The_Same_Instant()
    {
        // SQLite compares the stored timestamps as text, so a retry time kept in its own offset
        // would sort by its wall-clock digits: a past time 14 hours ahead would look not yet due,
        // and a future time 12 hours behind would look due.
        var store = await StoreAsync();
        await store.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        await Task.Delay(5);
        await store.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));
        var batch = await store.ClaimPendingAsync(10, lease, CancellationToken.None);
        batch.Should().HaveCount(2);
        var due = batch[0].Id;
        var notDue = batch[1].Id;

        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToOffset(TimeSpan.FromHours(14));
        var future = DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(-12));
        (await store.MarkFailedAsync(due, 1, past, lease, CancellationToken.None)).Should().BeTrue();
        (await store.MarkFailedAsync(notDue, 1, future, lease, CancellationToken.None)).Should().BeTrue();

        (await store.ClaimPendingAsync(10, lease, CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(due);
        (await ReadTextAsync(due, "NextRetryAt")).Should().EndWith("+00:00", "timestamps are stored in UTC");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_Lease_Without_A_Host_Is_Rejected(string? hostId)
    {
        var store = await StoreAsync();
        var lease = new OutboxLease(hostId!, TimeSpan.FromMinutes(1));
        var id = OutboxMessageId.New();

        var calls = new Func<Task>[]
        {
            async () => await store.ClaimPendingAsync(10, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.RenewLeaseAsync(id, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.ReleaseLeasesAsync([id], lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.MarkSucceededAsync(id, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.DeadLetterAsync(id, "boom", lease, CancellationToken.None).ConfigureAwait(false),
        };

        foreach (var call in calls)
            await call.Should().ThrowAsync<ArgumentException>().WithParameterName("lease");
    }

    private async Task<OrmOutboxStore> StoreAsync()
        => new(await ConnectAsync().ConfigureAwait(false));

    private async Task<IAsyncDbConnection> ConnectAsync()
    {
        var raw = await OpenRawAsync().ConfigureAwait(false);
        var connection = raw.AsAsync();
        _connections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Opens a connection that waits for a busy writer instead of failing, since SQLite
    /// serializes writers and eight claimers will contend.
    /// </summary>
    private async Task<SqliteConnection> OpenRawAsync()
    {
        var raw = new SqliteConnection($"Data Source={_path}");
        await raw.OpenAsync().ConfigureAwait(false);
        var pragma = raw.CreateCommand();
        await using (pragma.ConfigureAwait(false))
        {
            pragma.CommandText = "PRAGMA busy_timeout = 30000;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return raw;
    }

    private Task<long> ReadStatusAsync(OutboxMessageId id) => ReadColumnAsync(id, "Status");

    private Task<long> ReadRetryCountAsync(OutboxMessageId id) => ReadColumnAsync(id, "RetryCount");

    private async Task<long> ReadColumnAsync(OutboxMessageId id, string column)
    {
        var scalar = await ReadScalarAsync(id, column).ConfigureAwait(false);
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A column's stored value as text, or null when it is NULL.</summary>
    private async Task<string?> ReadTextAsync(OutboxMessageId id, string column)
    {
        var scalar = await ReadScalarAsync(id, column).ConfigureAwait(false);
        return scalar is null or DBNull
            ? null
            : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<object?> ReadScalarAsync(OutboxMessageId id, string column)
    {
        var raw = await OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = $"SELECT {column} FROM OutboxMessages WHERE Id = $id";
                cmd.Parameters.AddWithValue("$id", id.Value);
                return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }
    }
}
