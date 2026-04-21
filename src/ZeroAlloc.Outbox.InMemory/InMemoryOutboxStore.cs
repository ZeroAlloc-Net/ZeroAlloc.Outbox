using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;

namespace ZeroAlloc.Outbox.InMemory;

/// <summary>Thread-safe in-memory <see cref="IOutboxStore"/> for use in tests.</summary>
public sealed class InMemoryOutboxStore : IOutboxStore, IOutboxDashboardStore
{
    private readonly ConcurrentDictionary<Guid, InMemoryOutboxEntry> _entries = new();
    private readonly ConcurrentDictionary<DateTimeOffset, ThroughputAccumulator> _throughput = new();

    public ValueTask EnqueueAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        var entry = new InMemoryOutboxEntry
        {
            Id = Guid.NewGuid(),
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

    public ValueTask MarkSucceededAsync(Guid id, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.Status = InMemoryEntryStatus.Succeeded;
            entry.ProcessedAt = DateTimeOffset.UtcNow;
            BumpThroughput(isDispatched: true);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(Guid id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.Status = InMemoryEntryStatus.Pending;
            entry.RetryCount = retryCount;
            entry.NextRetryAt = nextRetryAt;
            BumpThroughput(isDispatched: false);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DeadLetterAsync(Guid id, string error, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.Status = InMemoryEntryStatus.DeadLetter;
            entry.DeadLetterError = error;
            BumpThroughput(isDispatched: false);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Exposes all entries for test assertions.</summary>
    public IReadOnlyList<InMemoryOutboxEntry> AllEntries() => _entries.Values.ToList();

    public ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct)
    {
        var pending = new List<OutboxMessageView>();
        var retry = new List<OutboxMessageView>();
        var dead = new List<OutboxMessageView>();
        var dispatched = new List<OutboxMessageView>();

        foreach (var e in _entries.Values)
        {
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

    public async IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var ordered = _throughput.ToArray();
        Array.Sort(ordered, static (a, b) => a.Key.CompareTo(b.Key));
        foreach (var kv in ordered)
        {
            if (kv.Key < cutoff) continue;
            ct.ThrowIfCancellationRequested();
            yield return new ThroughputPoint(kv.Key, kv.Value.Dispatched, kv.Value.Failed);
            await Task.Yield();
        }
    }

    public ValueTask RequeueAsync(Guid id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Message {id} not found.");
        if (entry.Status != InMemoryEntryStatus.DeadLetter)
            throw new InvalidOperationException($"Message {id} is not dead-lettered.");
        entry.Status = InMemoryEntryStatus.Pending;
        entry.RetryCount = 0;
        entry.NextRetryAt = DateTimeOffset.UtcNow;
        entry.DeadLetterError = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask CancelAsync(Guid id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Message {id} not found.");
        if (entry.Status != InMemoryEntryStatus.Pending)
            throw new InvalidOperationException($"Message {id} cannot be cancelled (status: {entry.Status}).");
        _entries.TryRemove(id, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask ForceDispatchAsync(Guid id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
            throw new InvalidOperationException($"Message {id} not found.");
        if (entry.Status != InMemoryEntryStatus.Pending)
            throw new InvalidOperationException($"Message {id} is not pending (status: {entry.Status}).");
        entry.NextRetryAt = DateTimeOffset.UtcNow;
        return ValueTask.CompletedTask;
    }

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

    private static OutboxMessageView ToView(InMemoryOutboxEntry e) => new()
    {
        Id = e.Id,
        TypeName = e.TypeName,
        CreatedAt = e.CreatedAt,
        RetryCount = e.RetryCount,
        NextRetryAt = e.NextRetryAt,
        PayloadPreview = DecodePreview(e.Payload),
        DispatchedAt = e.Status == InMemoryEntryStatus.Succeeded ? e.ProcessedAt : null,
        DeadLetterError = e.Status == InMemoryEntryStatus.DeadLetter ? e.DeadLetterError : null,
    };

    private static string DecodePreview(byte[] payload) =>
        Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 200));

    public enum InMemoryEntryStatus { Pending, Succeeded, DeadLetter }

    public sealed class InMemoryOutboxEntry
    {
        public Guid Id { get; init; }
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
