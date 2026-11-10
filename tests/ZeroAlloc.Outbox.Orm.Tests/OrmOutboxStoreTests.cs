using System.Data;
using System.Data.Async.Adapters;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Behaviour of <see cref="OrmOutboxStore"/> against a real SQLite database
/// with the schema applied by the ORM's MigrationRunner.
/// </summary>
public sealed class OrmOutboxStoreTests
{
    private static readonly OutboxLease s_lease = new("test-host", TimeSpan.FromMinutes(1));
    private static readonly byte[] s_payload = [1, 2, 3];

    [Fact]
    public async Task Enqueue_Then_ClaimPending_Round_Trips()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);

        var pending = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);

        pending.Should().HaveCount(1);
        pending[0].TypeName.Should().Be("Test.Cmd");
        pending[0].RawPayload.Should().Equal(s_payload);
        pending[0].RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task ClaimPending_Honours_BatchSize_And_Orders_By_CreatedAt()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        for (var i = 0; i < 5; i++)
        {
            await store.EnqueueAsync($"Cmd.{i}", s_payload, transaction: null, CancellationToken.None);
        }

        var batch = await store.ClaimPendingAsync(3, s_lease, CancellationToken.None);

        batch.Should().HaveCount(3);
        batch.Should().BeInAscendingOrder(e => e.CreatedAt);
    }

    [Fact]
    public async Task MarkSucceeded_Removes_The_Message_From_Pending()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        var entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None))[0];

        await store.MarkSucceededAsync(entry.Id, s_lease, CancellationToken.None);

        (await store.ClaimPendingAsync(10, s_lease, CancellationToken.None)).Should().BeEmpty();
        (await fx.CountAsync()).Should().Be(1, "a dispatched message is kept, not deleted");
    }

    [Fact]
    public async Task MarkFailed_Keeps_It_Pending_But_Defers_The_Next_Attempt()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        var entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None))[0];

        await store.MarkFailedAsync(
            entry.Id, retryCount: 1, DateTimeOffset.UtcNow.AddMinutes(5), s_lease, CancellationToken.None);

        // NextRetryAt is in the future, so the poller must not see it yet.
        (await store.ClaimPendingAsync(10, s_lease, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Failed_Message_Returns_Once_Its_Retry_Time_Passes()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        var entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None))[0];

        await store.MarkFailedAsync(
            entry.Id, retryCount: 1, DateTimeOffset.UtcNow.AddSeconds(-1), s_lease, CancellationToken.None);

        var again = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);

        again.Should().HaveCount(1);
        again[0].RetryCount.Should().Be(1, "the attempt count must survive the round-trip");
    }

    [Fact]
    public async Task DeadLetter_Removes_It_From_Pending()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        var entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None))[0];

        await store.DeadLetterAsync(entry.Id, "boom", s_lease, CancellationToken.None);

        (await store.ClaimPendingAsync(10, s_lease, CancellationToken.None)).Should().BeEmpty();
        (await fx.CountAsync()).Should().Be(1, "a dead letter is kept for inspection");
    }

    [Fact]
    public async Task An_Illegal_Transition_Changes_Nothing_And_Reports_False()
    {
        // Dispatched is terminal. Marking it succeeded twice would otherwise
        // overwrite ProcessedAt and hide that something dispatched it twice.
        // The conditional update refuses it and says so, rather than throwing.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        var entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None))[0];
        await store.MarkSucceededAsync(entry.Id, s_lease, CancellationToken.None);

        (await store.MarkSucceededAsync(entry.Id, s_lease, CancellationToken.None)).Should().BeFalse();
        (await store.MarkFailedAsync(entry.Id, 1, DateTimeOffset.UtcNow, s_lease, CancellationToken.None)).Should().BeFalse();
        (await store.DeadLetterAsync(entry.Id, "late", s_lease, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Marking_A_Message_That_No_Longer_Exists_Is_A_No_Op()
    {
        // Matches the EF Core adapter: a message someone else removed is not an
        // error for a dispatcher that is merely finishing its work.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        (await store.MarkSucceededAsync(OutboxMessageId.New(), s_lease, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Enqueue_In_A_Transaction_Commits_With_The_Caller()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var raw = await fx.OpenRawAsync();
        await using (raw.ConfigureAwait(false))
        {
            var tx = (await raw.BeginTransactionAsync()) as System.Data.Common.DbTransaction;
            await store.EnqueueAsync("Test.Cmd", s_payload, tx, CancellationToken.None);
            await tx!.CommitAsync();
        }

        (await fx.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_In_A_Transaction_Rolls_Back_With_The_Caller()
    {
        // The point of accepting a DbTransaction. If the enqueue quietly ran on
        // its own connection it would survive the rollback, and the system would
        // dispatch a message describing work that never happened.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var raw = await fx.OpenRawAsync();
        await using (raw.ConfigureAwait(false))
        {
            var tx = (await raw.BeginTransactionAsync()) as System.Data.Common.DbTransaction;
            await store.EnqueueAsync("Test.Cmd", s_payload, tx, CancellationToken.None);
            await tx!.RollbackAsync();
        }

        (await fx.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueInTransaction_Commits_With_The_Callers_Orm_Transaction()
    {
        // The transaction an ORM caller holds, straight from BeginTransactionAsync, with no
        // unwrapping to a DbTransaction first.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var caller = await fx.ConnectAsync();
        await using (caller.ConfigureAwait(false))
        {
            var tx = await caller.BeginTransactionAsync();
            await using (tx.ConfigureAwait(false))
            {
                await store.EnqueueInTransactionAsync("Test.Cmd", s_payload, tx, CancellationToken.None);
                await tx.CommitAsync();
            }
        }

        var pending = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);
        pending.Should().ContainSingle();
        pending[0].TypeName.Should().Be("Test.Cmd");
        pending[0].RawPayload.Should().Equal(s_payload);
    }

    [Fact]
    public async Task EnqueueInTransaction_Rolls_Back_With_The_Callers_Orm_Transaction()
    {
        // The insert has to run on the transaction's own connection. On the store's injected
        // connection it would survive the rollback.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var caller = await fx.ConnectAsync();
        await using (caller.ConfigureAwait(false))
        {
            var tx = await caller.BeginTransactionAsync();
            await using (tx.ConfigureAwait(false))
            {
                await store.EnqueueInTransactionAsync("Test.Cmd", s_payload, tx, CancellationToken.None);
                await tx.RollbackAsync();
            }
        }

        (await fx.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueInTransaction_Enlists_Several_Rows_In_One_Transaction()
    {
        // A saga step can enlist more than one command. Each insert reuses the caller's open
        // transaction instead of committing or starting one of its own.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var caller = await fx.ConnectAsync();
        await using (caller.ConfigureAwait(false))
        {
            var tx = await caller.BeginTransactionAsync();
            await using (tx.ConfigureAwait(false))
            {
                await store.EnqueueInTransactionAsync("First", s_payload, tx, CancellationToken.None);
                await store.EnqueueInTransactionAsync("Second", s_payload, tx, CancellationToken.None);
                await tx.CommitAsync();
            }
        }

        (await fx.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task EnqueueInTransaction_Rejects_A_Null_Transaction()
    {
        // No transaction means an immediate write, which EnqueueAsync already spells out with
        // null. This method exists only to enlist, so a null here is a caller bug.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var act = async () => await store.EnqueueInTransactionAsync(
            "Test.Cmd", s_payload, null!, CancellationToken.None).ConfigureAwait(false);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("transaction");
        (await fx.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueDeferred_Falls_Back_To_Writing_Immediately()
    {
        // This adapter deliberately does not override EnqueueDeferredAsync,
        // because it has no ambient unit of work to defer into. The interface
        // default routes it to EnqueueAsync with no transaction.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        IOutboxStore store = new OrmOutboxStore(await fx.ConnectAsync());

        await store.EnqueueDeferredAsync("Test.Cmd", s_payload, CancellationToken.None);

        (await fx.CountAsync()).Should().Be(1);
    }

    private static async Task<string> ReadRowAsync(SqliteFixture fx)
    {
        var raw = await fx.OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText =
                    "SELECT Id, Status, RetryCount, DeadLetterError, ProcessedAt, NextRetryAt, LockedBy, LockedUntil "
                    + "FROM OutboxMessages";
                var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    (await reader.ReadAsync().ConfigureAwait(false)).Should().BeTrue();
                    var sb = new System.Text.StringBuilder();
                    for (var i = 0; i < reader.FieldCount; i++)
                        sb.Append(reader.GetValue(i)).Append('|');
                    return sb.ToString();
                }
            }
        }
    }

    private static async Task<OutboxMessageId> DeadLetteredAsync(OrmOutboxStore store)
    {
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None)
            .ConfigureAwait(false);
        var entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None).ConfigureAwait(false))[0];
        await store.MarkFailedAsync(entry.Id, 3, DateTimeOffset.UtcNow.AddSeconds(-1), s_lease, CancellationToken.None)
            .ConfigureAwait(false);
        entry = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None).ConfigureAwait(false))[0];
        (await store.DeadLetterAsync(entry.Id, "boom", s_lease, CancellationToken.None).ConfigureAwait(false))
            .Should().BeTrue();
        return entry.Id;
    }

    private static async Task<(long Status, long RetryCount, bool Clean)> ReadResetAsync(SqliteFixture fx)
    {
        var raw = await fx.OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText =
                    "SELECT Status, RetryCount, "
                    + "DeadLetterError IS NULL AND ProcessedAt IS NULL AND LockedBy IS NULL AND LockedUntil IS NULL "
                    + "FROM OutboxMessages";
                var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    (await reader.ReadAsync().ConfigureAwait(false)).Should().BeTrue();
                    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) == 1);
                }
            }
        }
    }

    public enum RequeueSetup { Pending, Leased, Retry, Succeeded }

    [Fact]
    public async Task Requeue_moves_a_dead_lettered_row_back_to_pending_due_now()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        var id = await DeadLetteredAsync(store);

        await ((IOutboxRequeue)(object)store).RequeueAsync(id, CancellationToken.None);

        var row = await ReadResetAsync(fx);
        row.Status.Should().Be(0);
        row.RetryCount.Should().Be(0);
        row.Clean.Should().BeTrue("DeadLetterError, ProcessedAt and the lease are cleared");
        var claimed = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);
        claimed.Should().ContainSingle().Which.Id.Should().Be(id);
        claimed[0].RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task Requeue_clears_a_stale_foreign_lease_and_the_row_is_claimable()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        var id = await DeadLetteredAsync(store);
        var raw = await fx.OpenRawAsync();
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "UPDATE OutboxMessages SET LockedBy = 'other-host', LockedUntil = @until";
                cmd.Parameters.AddWithValue("@until", DateTimeOffset.UtcNow.AddHours(-1));
                (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
            }
        }

        await ((IOutboxRequeue)(object)store).RequeueAsync(id, CancellationToken.None);

        (await ReadResetAsync(fx)).Clean.Should().BeTrue("LockedBy and LockedUntil are cleared");
        (await store.ClaimPendingAsync(10, s_lease, CancellationToken.None)).Should().ContainSingle()
            .Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task Requeue_of_an_unknown_id_throws_not_found()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        var id = OutboxMessageId.New();

        var act = async () => await ((IOutboxRequeue)(object)store).RequeueAsync(id, CancellationToken.None)
            .ConfigureAwait(false);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"Message {id} not found.");
    }

    [Theory]
    [InlineData(RequeueSetup.Pending, "Pending")]
    [InlineData(RequeueSetup.Leased, "Pending")] // the claim itself leaves the row leased
    [InlineData(RequeueSetup.Retry, "Retry")]
    [InlineData(RequeueSetup.Succeeded, "Dispatched")]
    public async Task Requeue_of_a_pending_leased_or_succeeded_row_throws_and_changes_nothing(
        RequeueSetup setup, string expectedState)
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        // Claiming leases the row; the Pending case reads the id from a claim that is then released.
        var id = (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None))[0].Id;
        switch (setup)
        {
            case RequeueSetup.Pending:
                await store.ReleaseLeasesAsync([id], s_lease, CancellationToken.None);
                break;
            case RequeueSetup.Retry:
                await store.MarkFailedAsync(id, 2, DateTimeOffset.UtcNow, s_lease, CancellationToken.None);
                break;
            case RequeueSetup.Succeeded:
                await store.MarkSucceededAsync(id, s_lease, CancellationToken.None);
                break;
        }

        var before = await ReadRowAsync(fx);
        var act = async () => await ((IOutboxRequeue)(object)store).RequeueAsync(id, CancellationToken.None)
            .ConfigureAwait(false);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"Message {id} cannot be requeued in state {expectedState}.");
        (await ReadRowAsync(fx)).Should().Be(before);
    }

    [Fact]
    public async Task EnqueueInTransaction_Runs_On_The_Transactions_Connection_Not_The_Stores()
    {
        // The rollback test proves the row follows the caller's transaction; this pins which
        // connection the insert's command was created on.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var own = new RecordingAsyncConnection(await fx.ConnectAsync());
        var store = new OrmOutboxStore(own);

        var caller = new RecordingAsyncConnection(await fx.ConnectAsync());
        await using (caller.ConfigureAwait(false))
        {
            var tx = await caller.BeginTransactionAsync();
            await using (tx.ConfigureAwait(false))
            {
                await store.EnqueueInTransactionAsync("Test.Cmd", s_payload, tx, CancellationToken.None);
                await tx.CommitAsync();
            }
        }

        own.Commands.Should().BeEmpty();
        (await fx.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_Connection_That_Was_Open_Stays_Open()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var connection = await fx.ConnectAsync();
        var store = new OrmOutboxStore(connection);

        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        var claimed = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);
        await store.MarkSucceededAsync(claimed[0].Id, s_lease, CancellationToken.None);

        connection.State.Should().Be(ConnectionState.Open);
    }

    [Fact]
    public async Task A_Connection_The_Store_Opened_Is_Closed_Again()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var raw = new SqliteConnection(fx.ConnectionString);
        await using var _ = raw.ConfigureAwait(false);
        var connection = raw.AsAsync();
        var store = new OrmOutboxStore(connection);

        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        connection.State.Should().Be(ConnectionState.Closed);

        var claimed = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);
        claimed.Should().ContainSingle();
        connection.State.Should().Be(ConnectionState.Closed);

        (await store.MarkSucceededAsync(claimed[0].Id, s_lease, CancellationToken.None)).Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task The_Enqueue_Time_Reads_Back_As_The_Same_Utc_Instant()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var before = DateTimeOffset.UtcNow;
        await store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        var claimed = await store.ClaimPendingAsync(10, s_lease, CancellationToken.None);
        var createdAt = claimed.Should().ContainSingle().Which.CreatedAt;
        createdAt.Offset.Should().Be(TimeSpan.Zero);
        createdAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
