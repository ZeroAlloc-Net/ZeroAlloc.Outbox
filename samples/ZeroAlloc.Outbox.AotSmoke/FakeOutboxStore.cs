using System;
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
        => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

    public ValueTask MarkSucceededAsync(Guid id, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask MarkFailedAsync(Guid id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask DeadLetterAsync(Guid id, string error, CancellationToken ct) => ValueTask.CompletedTask;
}
