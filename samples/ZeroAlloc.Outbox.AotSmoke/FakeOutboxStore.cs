using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

// Minimal IOutboxStore for the AOT smoke — only EnqueueAsync is used; the rest
// of the interface is required for type wiring but never called from this sample.
internal sealed class FakeOutboxStore : IOutboxStore
{
    public readonly List<(string TypeName, ReadOnlyMemory<byte> Payload)> Recorded = new();

    public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
    {
        Recorded.Add((typeName, payload));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);

    public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct) => ValueTask.CompletedTask;
}
