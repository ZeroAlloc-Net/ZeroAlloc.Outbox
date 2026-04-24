using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Benchmarks;

// Measures the generator-emitted OrderPlacedOutboxWriter.WriteAsync against a
// fake in-memory store. The claim: the writer itself is a thin forwarder —
// it should allocate only what the serializer allocates. With a fake
// serializer that pre-allocates its output, the writer's own path is 0 B/op.
[MemoryDiagnoser]
[SimpleJob]
public class WriteAsyncBenchmark
{
    private FakeStore _store = null!;
    private FakeSerializer _serializer = null!;
    private OrderPlacedOutboxWriter _writer = null!;
    private OrderPlaced _message = null!;

    [GlobalSetup]
    public void Setup()
    {
        _store = new FakeStore();
        _serializer = new FakeSerializer();
        _writer = new OrderPlacedOutboxWriter(_store, _serializer);
        _message = new OrderPlaced("ord-1", 99.95m);
    }

    [Benchmark]
    public async ValueTask WriteAsync()
        => await _writer.WriteAsync(_message, transaction: null, CancellationToken.None).ConfigureAwait(false);
}

[OutboxMessage]
public sealed record OrderPlaced(string OrderId, decimal Total);

internal sealed class FakeStore : IOutboxStore
{
    public int WriteCount;
    public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
    {
        Interlocked.Increment(ref WriteCount);
        return ValueTask.CompletedTask;
    }
    public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);
    public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct) => ValueTask.CompletedTask;
}

internal sealed class FakeSerializer : IOutboxSerializer
{
    private static readonly byte[] s_payload = new byte[32];

    [RequiresUnreferencedCode("Fake — no reflection.")]
    [RequiresDynamicCode("Fake — no reflection.")]
    public ReadOnlyMemory<byte> Serialize<T>(T value) => s_payload;

    [RequiresUnreferencedCode("Fake — no reflection.")]
    [RequiresDynamicCode("Fake — no reflection.")]
    public T Deserialize<T>(ReadOnlyMemory<byte> data) => default!;
}
