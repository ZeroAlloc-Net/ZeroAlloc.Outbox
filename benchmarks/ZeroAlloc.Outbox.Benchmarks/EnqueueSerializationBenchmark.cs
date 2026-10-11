using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using System.Data.Async.Adapters;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;

namespace ZeroAlloc.Outbox.Benchmarks;

// Measures the enqueue a consumer runs: the generator-emitted writer, DispatchingOutboxSerializer
// over the SerializerDispatcher that ZeroAlloc.Serialisation generates for this assembly, and a
// store. Against FakeStore, which keeps nothing, the Allocated column is what serialization costs;
// against OrmOutboxStore over SQLite in memory it is the whole enqueue.
[MemoryDiagnoser]
[SimpleJob]
public class EnqueueSerializationBenchmark
{
    private static readonly SerializedOrderPaid s_struct = new(42, 1234567890L, Express: true);
    private static readonly SerializedOrderShipped s_class = new("order-42", 99.95m);

    private SerializedOrderPaidOutboxWriter _structWriter = null!;
    private SerializedOrderShippedOutboxWriter _classWriter = null!;
    private SerializedOrderShippedOutboxWriter _ormClassWriter = null!;
    private SqliteConnection _raw = null!;

    [GlobalSetup]
    public void Setup()
    {
        var serializer = new DispatchingOutboxSerializer(new SerializerDispatcher());
        var fake = new FakeStore();
        _structWriter = new SerializedOrderPaidOutboxWriter(fake, serializer);
        _classWriter = new SerializedOrderShippedOutboxWriter(fake, serializer);

        _raw = new SqliteConnection("Data Source=:memory:");
        _raw.Open();
        var connection = _raw.AsAsync();
        new MigrationRunner(connection, OutboxOrmMigrations.Sqlite, new SqliteMigrationDialect())
            .RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        _ormClassWriter = new SerializedOrderShippedOutboxWriter(new OrmOutboxStore(connection), serializer);
    }

    [GlobalCleanup]
    public void Cleanup() => _raw.Dispose();

    [Benchmark(Description = "Serialize and enqueue a struct, fake store")]
    public ValueTask StructMessage()
        => _structWriter.WriteAsync(s_struct, transaction: null, CancellationToken.None);

    [Benchmark(Description = "Serialize and enqueue a class, fake store")]
    public ValueTask ClassMessage()
        => _classWriter.WriteAsync(s_class, transaction: null, CancellationToken.None);

    [Benchmark(Description = "Serialize and enqueue a class, ORM store")]
    public ValueTask ClassMessageOrm()
        => _ormClassWriter.WriteAsync(s_class, transaction: null, CancellationToken.None);
}
