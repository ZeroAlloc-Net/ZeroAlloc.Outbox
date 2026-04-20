using System.Collections.Concurrent;
using System.Data.Common;

namespace ZeroAlloc.Outbox.InMemory;

/// <summary>Thread-safe in-memory <see cref="IOutboxStore"/> for use in tests.</summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, InMemoryOutboxEntry> _entries = new();

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
            entry.Status = InMemoryEntryStatus.Succeeded;
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(Guid id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.Status = InMemoryEntryStatus.Pending;
            entry.RetryCount = retryCount;
            entry.NextRetryAt = nextRetryAt;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DeadLetterAsync(Guid id, string error, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.Status = InMemoryEntryStatus.DeadLetter;
        return ValueTask.CompletedTask;
    }

    /// <summary>Exposes all entries for test assertions.</summary>
    public IReadOnlyList<InMemoryOutboxEntry> AllEntries() => _entries.Values.ToList();

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
    }
}
