using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Benchmarks;

// Measures the overhead ZA.Outbox adds on top of a correctness-matched hand-
// rolled outbox: transactional enqueue, claim-then-dispatch-then-commit. Both
// rows use the same SQLite-in-memory connection (different tables) so storage
// variance cancels out — the delta is the cost of the IOutboxWriter<T> +
// IOutboxStore + serializer abstraction layer.

[MemoryDiagnoser]
[SimpleJob]
public class HandRolledOverheadBenchmark
{
    private SqliteConnection _conn = null!;
    private HandRolledOutbox _hand = null!;
    private SqliteDirectStore _zaStore = null!;
    private FakeSerializer _serializer = null!;
    private OrderPlacedOutboxWriter _zaWriter = null!;
    private OrderPlaced _message = null!;
    private static readonly byte[] s_payload = new byte[32];

    [GlobalSetup]
    public void Setup()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        _hand = new HandRolledOutbox(_conn);
        _zaStore = new SqliteDirectStore(_conn);
        _serializer = new FakeSerializer();
        _zaWriter = new OrderPlacedOutboxWriter(_zaStore, _serializer);
        _message = new OrderPlaced("ord-1", 99.95m);
    }

    [GlobalCleanup]
    public void Cleanup() => _conn.Dispose();

    // --- Enqueue (single message) ---

    [Benchmark(Baseline = true, Description = "Hand-rolled: enqueue 1 message")]
    [BenchmarkCategory("Enqueue1")]
    public void Hand_Enqueue() => _hand.Enqueue("OrderPlaced", s_payload);

    [Benchmark(Description = "ZA.Outbox: enqueue 1 message")]
    [BenchmarkCategory("Enqueue1")]
    public ValueTask Za_Enqueue()
        => _zaWriter.WriteAsync(_message, transaction: null, CancellationToken.None);

    // --- Dispatch tick (10 messages) ---
    //
    // [IterationSetup] preloads exactly 10 pending rows into each store so the
    // measured method runs only the dispatch round (SELECT → handle → UPDATE).

    [IterationSetup(Targets = [nameof(Hand_DispatchTick), nameof(Za_DispatchTick)])]
    public void PrepareDispatchTick()
    {
        // Clear any leftover state from previous iteration
        using (var clear = _conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM hand_outbox; DELETE FROM za_outbox;";
            clear.ExecuteNonQuery();
        }
        // Preload 10 messages each
        for (var i = 0; i < 10; i++)
        {
            _hand.Enqueue("OrderPlaced", s_payload);
            _zaStore.EnqueueAsync("OrderPlaced", s_payload, transaction: null, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Benchmark(Description = "Hand-rolled: dispatch tick (10 messages)")]
    [BenchmarkCategory("DispatchTick10")]
    public int Hand_DispatchTick() => _hand.DispatchTick(10, static (_, _) => { });

    [Benchmark(Description = "ZA.Outbox: dispatch tick (10 messages)")]
    [BenchmarkCategory("DispatchTick10")]
    public async ValueTask<int> Za_DispatchTick()
    {
        var entries = await _zaStore.FetchPendingAsync(10, CancellationToken.None).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            // simulated handler — no-op (matches the hand-rolled side)
            _ = entry.Payload;
            await _zaStore.MarkSucceededAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
        }
        return entries.Count;
    }
}
