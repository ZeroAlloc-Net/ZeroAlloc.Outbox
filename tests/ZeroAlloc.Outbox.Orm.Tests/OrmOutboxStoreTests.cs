using AwesomeAssertions;
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
}
