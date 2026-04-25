using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using ZeroAlloc.Collections;

namespace ZeroAlloc.Outbox.InMemory;

/// <summary>
/// Thread-safe in-memory <see cref="IOutboxStore"/> for use in tests and examples.
/// Not suitable for long-running production processes — throughput buckets accumulate
/// indefinitely and there is no persistence across process restarts.
/// </summary>
/// <remarks>
/// Implements <see cref="IDisposable"/>: call <see cref="Dispose"/> when the store is no
/// longer needed so the pooled backing arrays are returned to <see cref="System.Buffers.ArrayPool{T}"/>.
/// The DI container disposes singleton registrations automatically on host shutdown.
/// </remarks>
public sealed class InMemoryOutboxStore : IOutboxStore, IOutboxDashboardStore, IDisposable
{
    private readonly ConcurrentHeapSpanDictionary<OutboxMessageId, InMemoryOutboxEntry> _entries = new();
    private readonly ConcurrentHeapSpanDictionary<DateTimeOffset, ThroughputAccumulator> _throughput = new();

    public ValueTask EnqueueAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        var entry = new InMemoryOutboxEntry
        {
            Id = OutboxMessageId.New(),
            TypeName = typeName,
            Payload = payload.ToArray(),
            RetryCount = 0,
            Status = InMemoryEntryStatus.Pending,
            NextRetryAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _entries[entry.Id] = entry;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<OutboxEntry>();
        foreach (var kv in _entries)
        {
            var e = kv.Value;
            if (e.Status == InMemoryEntryStatus.Pending && e.NextRetryAt <= now)
            {
                results.Add(new OutboxEntry
                {
                    Id = e.Id,
                    TypeName = e.TypeName,
                    RawPayload = e.Payload,
                    RetryCount = e.RetryCount,
                    CreatedAt = e.CreatedAt,
                });
                if (results.Count >= batchSize) break;
            }
        }
        return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(results);
    }

    public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            lock (entry)
            {
                var fsm = new OutboxMessageFsm(ToState(entry.Status, entry.RetryCount));
                if (!fsm.TryFire(OutboxMessageTrigger.Dispatch))
                    throw new InvalidOperationException(
                        $"Cannot mark message {id} as succeeded in state {fsm.Current}.");
                entry.Status = InMemoryEntryStatus.Succeeded;
                entry.ProcessedAt = DateTimeOffset.UtcNow;
            }
            BumpThroughput(isDispatched: true);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            lock (entry)
            {
                var fsm = new OutboxMessageFsm(ToState(entry.Status, entry.RetryCount));
                if (!fsm.TryFire(OutboxMessageTrigger.Fail))
                    throw new InvalidOperationException(
                        $"Cannot mark message {id} as failed in state {fsm.Current}.");
                entry.Status = InMemoryEntryStatus.Pending;
                entry.RetryCount = retryCount;
                entry.NextRetryAt = nextRetryAt;
            }
            BumpThroughput(isDispatched: false);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            lock (entry)
            {
                var fsm = new OutboxMessageFsm(ToState(entry.Status, entry.RetryCount));
                if (!fsm.TryFire(OutboxMessageTrigger.Exhaust))
                    throw new InvalidOperationException(
                        $"Cannot dead-letter message {id} in state {fsm.Current}.");
                entry.Status = InMemoryEntryStatus.DeadLetter;
                entry.DeadLetterError = error;
            }
            BumpThroughput(isDispatched: false);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Exposes all entries for test assertions.</summary>
    public IReadOnlyList<InMemoryOutboxEntry> AllEntries() =>
        _entries.Select(kv => kv.Value).ToList();

    public ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct)
    {
        var pending = new List<OutboxMessageView>();
        var retry = new List<OutboxMessageView>();
        var dead = new List<OutboxMessageView>();
        var dispatched = new List<OutboxMessageView>();

        foreach (var kv in _entries)
        {
            var e = kv.Value;
            var view = ToView(e);
            switch (e.Status)
            {
                case InMemoryEntryStatus.Pending when e.RetryCount == 0:
                    pending.Add(view);
                    break;
                case InMemoryEntryStatus.Pending:
                    retry.Add(view);
                    break;
                case InMemoryEntryStatus.DeadLetter:
                    dead.Add(view);
                    break;
                case InMemoryEntryStatus.Succeeded:
                    dispatched.Add(view);
                    break;
            }
        }

        var trimmedDispatched = dispatched
            .OrderByDescending(v => v.DispatchedAt ?? v.CreatedAt)
            .Take(dispatchedLimit)
            .ToList();

        return ValueTask.FromResult(new OutboxSnapshot(pending, retry, dead, trimmedDispatched));
    }

#pragma warning disable CS1998 // async method lacks 'await' — intentional for IAsyncEnumerable iterator
    public async IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct)
#pragma warning restore CS1998
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var ordered = _throughput.ToArray();
        Array.Sort(ordered, static (a, b) => a.Key.CompareTo(b.Key));
        foreach (var kv in ordered)
        {
            if (kv.Key < cutoff) continue;
            ct.ThrowIfCancellationRequested();
            yield return new ThroughputPoint(kv.Key, kv.Value.Dispatched, kv.Value.Failed);
        }
    }

    public ValueTask RequeueAsync(OutboxMessageId id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Message {id} not found.");
        lock (entry)
        {
            var fsm = new OutboxMessageFsm(ToState(entry.Status, entry.RetryCount));
            if (!fsm.TryFire(OutboxMessageTrigger.Requeue))
                throw new InvalidOperationException(
                    $"Message {id} cannot be requeued in state {fsm.Current}.");
            entry.Status = InMemoryEntryStatus.Pending;
            entry.RetryCount = 0;
            entry.NextRetryAt = DateTimeOffset.UtcNow;
            entry.DeadLetterError = null;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask CancelAsync(OutboxMessageId id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Message {id} not found.");
        lock (entry)
        {
            var fsm = new OutboxMessageFsm(ToState(entry.Status, entry.RetryCount));
            if (!fsm.TryFire(OutboxMessageTrigger.Cancel))
                throw new InvalidOperationException(
                    $"Message {id} cannot be cancelled in state {fsm.Current}.");
            _entries.TryRemove(id, out _);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ForceDispatchAsync(OutboxMessageId id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Message {id} not found.");
        lock (entry)
        {
            if (entry.Status != InMemoryEntryStatus.Pending)
                throw new InvalidOperationException($"Message {id} is not pending (status: {entry.Status}).");
            entry.NextRetryAt = DateTimeOffset.UtcNow;
        }
        return ValueTask.CompletedTask;
    }

    private static OutboxMessageState ToState(InMemoryEntryStatus status, int retryCount) => status switch
    {
        InMemoryEntryStatus.Succeeded => OutboxMessageState.Dispatched,
        InMemoryEntryStatus.DeadLetter => OutboxMessageState.DeadLetter,
        _ => retryCount == 0 ? OutboxMessageState.Pending : OutboxMessageState.Retry,
    };

    private void BumpThroughput(bool isDispatched)
    {
        var bucket = TruncateToMinute(DateTimeOffset.UtcNow);
        var acc = _throughput.GetOrAdd(bucket, _ => new ThroughputAccumulator());
        if (isDispatched)
            Interlocked.Increment(ref acc.Dispatched);
        else
            Interlocked.Increment(ref acc.Failed);
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset dto) =>
        new(dto.Year, dto.Month, dto.Day, dto.Hour, dto.Minute, 0, dto.Offset);

    private static OutboxMessageView ToView(InMemoryOutboxEntry e)
    {
        InMemoryEntryStatus status;
        int retryCount;
        DateTimeOffset nextRetryAt;
        DateTimeOffset? processedAt;
        string? deadLetterError;
        byte[] payload;
        lock (e)
        {
            status = e.Status;
            retryCount = e.RetryCount;
            nextRetryAt = e.NextRetryAt;
            processedAt = e.ProcessedAt;
            deadLetterError = e.DeadLetterError;
            payload = e.Payload;
        }
        return new OutboxMessageView
        {
            Id = e.Id,
            TypeName = e.TypeName,
            CreatedAt = e.CreatedAt,
            RetryCount = retryCount,
            NextRetryAt = nextRetryAt,
            PayloadPreview = DecodePreview(payload),
            DispatchedAt = status == InMemoryEntryStatus.Succeeded ? processedAt : null,
            DeadLetterError = status == InMemoryEntryStatus.DeadLetter ? deadLetterError : null,
        };
    }

    private static string DecodePreview(byte[] payload) =>
        Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 200));

    /// <summary>
    /// Returns the pooled backing arrays of both dictionaries to
    /// <see cref="System.Buffers.ArrayPool{T}"/>.
    /// </summary>
    public void Dispose()
    {
        _entries.Dispose();
        _throughput.Dispose();
    }

    public enum InMemoryEntryStatus { Pending, Succeeded, DeadLetter }

    public sealed class InMemoryOutboxEntry
    {
        public OutboxMessageId Id { get; init; }
        public required string TypeName { get; init; }
        public required byte[] Payload { get; set; }
        public int RetryCount { get; set; }
        public InMemoryEntryStatus Status { get; set; }
        public DateTimeOffset NextRetryAt { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ProcessedAt { get; set; }
        public string? DeadLetterError { get; set; }
    }

    private sealed class ThroughputAccumulator
    {
        public int Dispatched;
        public int Failed;
    }
}
