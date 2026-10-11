using System.Data.Async;
using System.Data.Async.Adapters;
using System.Data.Common;
using AwesomeAssertions;
using Xunit;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// What an enqueue allocates. Microsoft.Data.Sqlite completes its async calls synchronously, so
/// each enqueue runs on the test thread and <see cref="GC.GetAllocatedBytesForCurrentThread"/>
/// sees all of it. SQLite copies a bound blob into native memory, not onto the managed heap.
/// </summary>
public sealed class OrmOutboxStoreAllocationTests
{
    // Large enough that a copy of it dwarfs everything else an insert allocates.
    private static readonly byte[] s_payload = new byte[32 * 1024];

    [Fact]
    public async Task Enqueue_Binds_The_Serialized_Array_Without_Copying_It()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());

        var bytes = Measure(() => store.EnqueueAsync("Test.Cmd", s_payload, transaction: null, CancellationToken.None));

        bytes.Should().BeLessThan(s_payload.Length, "the payload array is bound as it is, not copied");
    }

    [Fact]
    public async Task EnqueueInTransaction_Binds_The_Serialized_Array_Without_Copying_It()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var connection = await fx.ConnectAsync();
        var store = new OrmOutboxStore(connection);
        var tx = await connection.BeginTransactionAsync();
        await using var _ = tx.ConfigureAwait(false);

        var bytes = Measure(() => store.EnqueueInTransactionAsync("Test.Cmd", s_payload, tx, CancellationToken.None));

        bytes.Should().BeLessThan(s_payload.Length, "the payload array is bound as it is, not copied");
    }

    [Fact]
    public async Task Enqueue_With_A_DbTransaction_On_The_Stores_Connection_Binds_Without_Copying()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var connection = (AdapterDbConnection)await fx.ConnectAsync();
        var store = new OrmOutboxStore(connection);
        var tx = await ((DbConnection)connection).BeginTransactionAsync();
        await using var _ = tx.ConfigureAwait(false);

        var bytes = Measure(() => store.EnqueueAsync("Test.Cmd", s_payload, tx, CancellationToken.None));

        bytes.Should().BeLessThan(s_payload.Length, "the payload array is bound as it is, not copied");
    }

    [Fact]
    public async Task Enqueue_Of_A_Slice_Still_Writes_Exactly_The_Slice()
    {
        // A payload that is part of a larger array cannot be bound as that array: the store
        // must copy exactly the slice.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        byte[] buffer = [9, 1, 2, 3, 9];

        await store.EnqueueAsync("Test.Cmd", buffer.AsMemory(1, 3), transaction: null, CancellationToken.None);

        var claimed = await store.ClaimPendingAsync(10, new OutboxLease("host", TimeSpan.FromMinutes(1)), CancellationToken.None);
        claimed.Should().ContainSingle().Which.RawPayload.Should().Equal(1, 2, 3);
    }

    private static long Measure(Func<ValueTask> enqueue)
    {
        for (var i = 0; i < 10; i++)
            Run(enqueue);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
            Run(enqueue);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
    }

    private static void Run(Func<ValueTask> enqueue)
    {
        var pending = enqueue();
        if (!pending.IsCompletedSuccessfully)
            throw new InvalidOperationException("SQLite should complete the enqueue synchronously.");
    }
}
