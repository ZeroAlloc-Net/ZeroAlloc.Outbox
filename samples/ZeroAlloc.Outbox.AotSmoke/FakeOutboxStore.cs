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

    public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);

    // Nothing is ever claimed, so no lease is ever held.
    public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
        => ValueTask.FromResult(false);

    public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
        => ValueTask.FromResult(0);

    // Nothing is ever claimed, so no mark ever moves a message.
    public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct) => ValueTask.FromResult(false);
    public ValueTask<bool> MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct) => ValueTask.FromResult(false);
    public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct) => ValueTask.FromResult(false);
}
