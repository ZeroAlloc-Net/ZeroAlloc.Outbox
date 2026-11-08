using System.Data.Async;
using System.Data.Async.Adapters;
using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// What an enqueue allocates. Microsoft.Data.Sqlite completes its async calls synchronously, so
/// each enqueue runs on the test thread and <see cref="AllocationGate"/>, which counts the
/// calling thread's allocations, sees all of it. SQLite copies a bound blob into native memory, not onto the managed heap.
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

    [Fact]
    public async Task Serializing_And_Enqueuing_A_Message_Copies_Its_Payload_Once()
    {
        // The whole enqueue a generated writer runs: DispatchingOutboxSerializer over the
        // SerializerDispatcher generated for this assembly, then the store. The serializer writes
        // into a reused writer and copies the bytes once into an exact-size array, which the store
        // binds as it is, so the payload appears on the heap once.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var store = new OrmOutboxStore(await fx.ConnectAsync());
        var serializer = new DispatchingOutboxSerializer(new SerializerDispatcher());
        var message = new LargeOrmMessage(new string('x', 32 * 1024));
        var payloadLength = serializer.Serialize(message).Length;

        var bytes = Measure(() => store.EnqueueAsync(
            "Test.Large", serializer.Serialize(message), transaction: null, CancellationToken.None));

        bytes.Should().BeLessThan(payloadLength + payloadLength / 2, "the payload is allocated once, not copied again");
    }

    // The store is scoped, so a worker poll and every request scope builds one. Its SQL is built
    // once per configuration, not per store: what is left is the store and its statement runner.
    private const long StoreBytes = 72; // the store, 40 bytes, and its statement runner, 32

    // The measured store must escape. Left unused, the Release JIT can stack-allocate it and the
    // gate reads 32 bytes instead of 72.
    private static OrmOutboxStore? s_sink;

    [Theory]
    [InlineData(OutboxOrmDialect.Sqlite, "OutboxMessages", false)]
    [InlineData(OutboxOrmDialect.Postgres, "\"OutboxMessages\"", true)]
    [InlineData(OutboxOrmDialect.SqlServer, "[Outbox]", true)]
    public async Task Constructing_A_Store_Builds_No_Sql(OutboxOrmDialect dialect, string table, bool quote)
    {
        await using var fx = new SqliteFixture();
        var connection = await fx.ConnectAsync();
        _ = new OrmOutboxStore(connection, dialect, table, quote); // fills the SQL cache

        var bytes = AllocationGate.MeasureBytesPerCall(
            100, () => s_sink = new OrmOutboxStore(connection, dialect, table, quote));

        bytes.Should().Be(StoreBytes);
    }

    [Fact]
    public async Task Resolving_A_Pipelines_Store_From_A_Scope_Builds_No_Sql()
    {
        await using var fx = new SqliteFixture();
        var services = new ServiceCollection();
        services.AddSingleton(await fx.ConnectAsync());
        services.AddOutbox("p", _ => { }).WithOrm(OutboxOrmDialect.Sqlite, o => o.TableName = "Custom");
        await using var sp = services.BuildServiceProvider();
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using (var warm = scopes.CreateScope())
            _ = warm.ServiceProvider.GetRequiredKeyedService<OrmOutboxStore>("p");

        var withScope = AllocationGate.MeasureBytesPerCall(100, () =>
        {
            using var scope = scopes.CreateScope();
            _ = scope.ServiceProvider.GetRequiredKeyedService<OrmOutboxStore>("p");
        });
        var scopeOnly = AllocationGate.MeasureBytesPerCall(100, () =>
        {
            using var scope = scopes.CreateScope();
        });

        (withScope - scopeOnly).Should().BeLessThan(300, "the store and its runner, with no SQL strings");
    }

    private static long Measure(Func<ValueTask> enqueue) =>
        AllocationGate.MeasureBytesPerCall(10, () => Run(enqueue));

    private static void Run(Func<ValueTask> enqueue)
    {
        var pending = enqueue();
        if (!pending.IsCompletedSuccessfully)
            throw new InvalidOperationException("SQLite should complete the enqueue synchronously.");
    }
}
