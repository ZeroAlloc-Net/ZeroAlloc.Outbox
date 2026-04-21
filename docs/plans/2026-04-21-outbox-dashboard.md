# ZeroAlloc.Outbox.Dashboard Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build an operations dashboard for `ZeroAlloc.Outbox` with real-time updates, delivered as a separate NuGet in the same repo.

**Architecture:** New `IOutboxDashboardStore` interface on the core package; `InMemory` and `EfCore` adapters implement it alongside their existing `IOutboxStore`. A new `ZeroAlloc.Outbox.Dashboard` project hosts Minimal API endpoints (snapshot, SSE, throughput, write actions) and an embedded HTML dashboard. Optional `ZeroAlloc.Outbox.Dashboard.Blazor` project provides a `<OutboxDashboard />` component. Dashboard events flow from the `OutboxWorkerService` via an in-process `Channel<OutboxDashboardEvent>`.

**Tech stack:** ASP.NET Core Minimal API, Server-Sent Events, `System.Threading.Channels`, vanilla JS (HTML), Blazor (component project only).

**Scope:** InMemory + EfCore adapters only. Redis adapter does not exist; deferred.

**Reference design:** [docs/plans/2026-04-21-outbox-dashboard-design.md](./2026-04-21-outbox-dashboard-design.md)

---

## Phase 1: Core types

### Task 1: Add `OutboxMessageView` and `OutboxSnapshot` types

**Files:**
- Create: `src/ZeroAlloc.Outbox/OutboxMessageView.cs`
- Create: `src/ZeroAlloc.Outbox/OutboxSnapshot.cs`
- Create: `src/ZeroAlloc.Outbox/ThroughputPoint.cs`
- Test: `tests/ZeroAlloc.Outbox.Tests/OutboxSnapshotTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.Outbox.Tests/OutboxSnapshotTests.cs
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Tests;

public class OutboxSnapshotTests
{
    [Fact]
    public void OutboxMessageView_InitializesWithRequiredProperties()
    {
        var view = new OutboxMessageView
        {
            Id = Guid.NewGuid(),
            TypeName = "OrderPlaced",
            CreatedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            NextRetryAt = DateTimeOffset.UtcNow,
            PayloadPreview = "{}",
        };
        Assert.Equal("OrderPlaced", view.TypeName);
    }

    [Fact]
    public void OutboxSnapshot_GroupsMessagesByState()
    {
        var snapshot = new OutboxSnapshot(
            Pending: Array.Empty<OutboxMessageView>(),
            RetryQueue: Array.Empty<OutboxMessageView>(),
            DeadLettered: Array.Empty<OutboxMessageView>(),
            Dispatched: Array.Empty<OutboxMessageView>());
        Assert.Empty(snapshot.Pending);
    }
}
```

**Step 2: Run to verify fail**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "OutboxSnapshotTests"`
Expected: compilation error — `OutboxMessageView` and `OutboxSnapshot` do not exist.

**Step 3: Implement**

```csharp
// src/ZeroAlloc.Outbox/OutboxMessageView.cs
namespace ZeroAlloc.Outbox;

/// <summary>Read-only projection of an outbox message for the dashboard.</summary>
public sealed record OutboxMessageView
{
    public required Guid Id { get; init; }
    public required string TypeName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int RetryCount { get; init; }
    public required DateTimeOffset NextRetryAt { get; init; }
    public required string PayloadPreview { get; init; }
    public DateTimeOffset? DispatchedAt { get; init; }
    public string? DeadLetterError { get; init; }
}
```

```csharp
// src/ZeroAlloc.Outbox/OutboxSnapshot.cs
namespace ZeroAlloc.Outbox;

public sealed record OutboxSnapshot(
    IReadOnlyList<OutboxMessageView> Pending,
    IReadOnlyList<OutboxMessageView> RetryQueue,
    IReadOnlyList<OutboxMessageView> DeadLettered,
    IReadOnlyList<OutboxMessageView> Dispatched);
```

```csharp
// src/ZeroAlloc.Outbox/ThroughputPoint.cs
namespace ZeroAlloc.Outbox;

public sealed record ThroughputPoint(DateTimeOffset Bucket, int Dispatched, int Failed);
```

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "OutboxSnapshotTests"`
Expected: 2/2 pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox/OutboxMessageView.cs src/ZeroAlloc.Outbox/OutboxSnapshot.cs src/ZeroAlloc.Outbox/ThroughputPoint.cs tests/ZeroAlloc.Outbox.Tests/OutboxSnapshotTests.cs
git commit -m "feat(outbox): add dashboard view types (OutboxMessageView, OutboxSnapshot, ThroughputPoint)"
```

---

### Task 2: Add `IOutboxDashboardStore` interface

**Files:**
- Create: `src/ZeroAlloc.Outbox/IOutboxDashboardStore.cs`
- Test: reuse `tests/ZeroAlloc.Outbox.Tests/OutboxSnapshotTests.cs`

**Step 1: Write the failing test**

```csharp
// Add to OutboxSnapshotTests.cs
[Fact]
public void IOutboxDashboardStore_DefinesExpectedSurface()
{
    var t = typeof(IOutboxDashboardStore);
    Assert.NotNull(t.GetMethod(nameof(IOutboxDashboardStore.GetSnapshotAsync)));
    Assert.NotNull(t.GetMethod(nameof(IOutboxDashboardStore.GetThroughputAsync)));
    Assert.NotNull(t.GetMethod(nameof(IOutboxDashboardStore.RequeueAsync)));
    Assert.NotNull(t.GetMethod(nameof(IOutboxDashboardStore.CancelAsync)));
    Assert.NotNull(t.GetMethod(nameof(IOutboxDashboardStore.ForceDispatchAsync)));
}
```

**Step 2: Run to verify fail**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "IOutboxDashboardStore"`
Expected: compilation error.

**Step 3: Implement**

```csharp
// src/ZeroAlloc.Outbox/IOutboxDashboardStore.cs
namespace ZeroAlloc.Outbox;

/// <summary>Read/write surface for the dashboard. Implemented alongside IOutboxStore.</summary>
public interface IOutboxDashboardStore
{
    ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct);
    IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(TimeSpan window, CancellationToken ct);

    /// <summary>Move a dead-lettered message back to pending. Throws if not dead-lettered.</summary>
    ValueTask RequeueAsync(Guid id, CancellationToken ct);

    /// <summary>Remove a pending or retry-queue message. Throws if dispatched or dead-lettered.</summary>
    ValueTask CancelAsync(Guid id, CancellationToken ct);

    /// <summary>Set NextRetryAt to now so the next polling cycle picks up the message.</summary>
    ValueTask ForceDispatchAsync(Guid id, CancellationToken ct);
}
```

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "IOutboxDashboardStore"`
Expected: pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox/IOutboxDashboardStore.cs tests/ZeroAlloc.Outbox.Tests/OutboxSnapshotTests.cs
git commit -m "feat(outbox): add IOutboxDashboardStore interface"
```

---

### Task 3: Add dashboard event types and `IOutboxDashboardEventPublisher`

**Files:**
- Create: `src/ZeroAlloc.Outbox/OutboxDashboardEvent.cs`
- Create: `src/ZeroAlloc.Outbox/IOutboxDashboardEventPublisher.cs`
- Create: `src/ZeroAlloc.Outbox/ChannelOutboxDashboardEventPublisher.cs`
- Test: `tests/ZeroAlloc.Outbox.Tests/DashboardEventPublisherTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.Outbox.Tests/DashboardEventPublisherTests.cs
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Tests;

public class DashboardEventPublisherTests
{
    [Fact]
    public async Task Publish_DeliversEventToSubscriber()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        var evt = new MessageDispatchedEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, 1);

        var reader = pub.Subscribe();
        await pub.PublishAsync(evt, CancellationToken.None);

        var received = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(evt.Id, ((MessageDispatchedEvent)received).Id);
    }

    [Fact]
    public async Task MultipleSubscribers_ReceiveSameEvent()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        var r1 = pub.Subscribe();
        var r2 = pub.Subscribe();
        var evt = new MessageCancelledEvent(Guid.NewGuid());

        await pub.PublishAsync(evt, CancellationToken.None);

        Assert.Equal(evt.Id, ((MessageCancelledEvent)await r1.ReadAsync()).Id);
        Assert.Equal(evt.Id, ((MessageCancelledEvent)await r2.ReadAsync()).Id);
    }
}
```

**Step 2: Run to verify fail**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "DashboardEventPublisher"`
Expected: compilation error.

**Step 3: Implement**

```csharp
// src/ZeroAlloc.Outbox/OutboxDashboardEvent.cs
namespace ZeroAlloc.Outbox;

public abstract record OutboxDashboardEvent(Guid Id);

public sealed record MessageQueuedEvent(Guid Id, string TypeName, DateTimeOffset QueuedAt) : OutboxDashboardEvent(Id);
public sealed record MessageDispatchedEvent(Guid Id, DateTimeOffset DispatchedAt, int AttemptCount) : OutboxDashboardEvent(Id);
public sealed record MessageFailedEvent(Guid Id, string Error, int AttemptCount, DateTimeOffset NextRetryAt) : OutboxDashboardEvent(Id);
public sealed record MessageDeadLetteredEvent(Guid Id, string Error, int TotalAttempts) : OutboxDashboardEvent(Id);
public sealed record MessageRequeuedEvent(Guid Id, DateTimeOffset RequeuedAt) : OutboxDashboardEvent(Id);
public sealed record MessageCancelledEvent(Guid Id) : OutboxDashboardEvent(Id);
```

```csharp
// src/ZeroAlloc.Outbox/IOutboxDashboardEventPublisher.cs
using System.Threading.Channels;

namespace ZeroAlloc.Outbox;

public interface IOutboxDashboardEventPublisher
{
    ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct);
    ChannelReader<OutboxDashboardEvent> Subscribe();
}
```

```csharp
// src/ZeroAlloc.Outbox/ChannelOutboxDashboardEventPublisher.cs
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Fan-out publisher. Each Subscribe() call returns a reader for a new bounded channel.
/// When a subscriber's channel is full, oldest events are dropped for that subscriber only.
/// </summary>
public sealed class ChannelOutboxDashboardEventPublisher : IOutboxDashboardEventPublisher
{
    private readonly ConcurrentDictionary<Guid, Channel<OutboxDashboardEvent>> _subscribers = new();

    public ChannelReader<OutboxDashboardEvent> Subscribe()
    {
        var ch = Channel.CreateBounded<OutboxDashboardEvent>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[Guid.NewGuid()] = ch;
        return ch.Reader;
    }

    public async ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct)
    {
        foreach (var ch in _subscribers.Values)
            await ch.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "DashboardEventPublisher"`
Expected: 2/2 pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox/OutboxDashboardEvent.cs src/ZeroAlloc.Outbox/IOutboxDashboardEventPublisher.cs src/ZeroAlloc.Outbox/ChannelOutboxDashboardEventPublisher.cs tests/ZeroAlloc.Outbox.Tests/DashboardEventPublisherTests.cs
git commit -m "feat(outbox): add dashboard event publisher with per-subscriber bounded channels"
```

---

## Phase 2: Store adapter implementations

### Task 4: Implement `IOutboxDashboardStore` on `InMemoryOutboxStore`

**Files:**
- Modify: `src/ZeroAlloc.Outbox.InMemory/InMemoryOutboxStore.cs`
- Test: `tests/ZeroAlloc.Outbox.Tests/InMemoryDashboardStoreTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.Outbox.Tests/InMemoryDashboardStoreTests.cs
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public class InMemoryDashboardStoreTests
{
    [Fact]
    public async Task GetSnapshotAsync_GroupsMessagesByState()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("Pending1", new byte[] { 1 }, null, default);
        await store.EnqueueAsync("Pending2", new byte[] { 2 }, null, default);

        IOutboxDashboardStore dashboard = store;
        var snapshot = await dashboard.GetSnapshotAsync(dispatchedLimit: 10, default);

        Assert.Equal(2, snapshot.Pending.Count);
        Assert.Empty(snapshot.DeadLettered);
        Assert.Empty(snapshot.Dispatched);
    }

    [Fact]
    public async Task RequeueAsync_MovesDeadLetterBackToPending()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var id = store.AllEntries()[0].Id;
        await store.DeadLetterAsync(id, "err", default);

        IOutboxDashboardStore dashboard = store;
        await dashboard.RequeueAsync(id, default);

        var snapshot = await dashboard.GetSnapshotAsync(10, default);
        Assert.Empty(snapshot.DeadLettered);
        Assert.Single(snapshot.Pending);
    }

    [Fact]
    public async Task CancelAsync_RemovesPendingMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var id = store.AllEntries()[0].Id;

        IOutboxDashboardStore dashboard = store;
        await dashboard.CancelAsync(id, default);

        Assert.Empty(store.AllEntries());
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDispatched()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var id = store.AllEntries()[0].Id;
        await store.MarkSucceededAsync(id, default);

        IOutboxDashboardStore dashboard = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dashboard.CancelAsync(id, default).AsTask());
    }

    [Fact]
    public async Task ForceDispatchAsync_SetsNextRetryToNow()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var id = store.AllEntries()[0].Id;
        await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow.AddHours(1), default);

        IOutboxDashboardStore dashboard = store;
        await dashboard.ForceDispatchAsync(id, default);

        var entry = store.AllEntries().Single(e => e.Id == id);
        Assert.True(entry.NextRetryAt <= DateTimeOffset.UtcNow);
    }
}
```

**Step 2: Run to verify fail**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "InMemoryDashboardStore"`
Expected: compilation error — `InMemoryOutboxStore` does not implement `IOutboxDashboardStore`.

**Step 3: Implement**

Modify `InMemoryOutboxStore.cs`:

1. Add `: IOutboxDashboardStore` to the class declaration
2. Add a concurrent ring buffer of throughput points (bucket=UtcNow truncated to 1 minute, dispatched count, failed count)
3. Implement the five interface methods

```csharp
// Add at top of file
using System.Runtime.CompilerServices;

// Change class declaration:
public sealed class InMemoryOutboxStore : IOutboxStore, IOutboxDashboardStore

// Add fields:
private readonly ConcurrentDictionary<DateTimeOffset, ThroughputAccumulator> _throughput = new();

// Track dispatch/fail at existing mutation points:
// In MarkSucceededAsync: after setting status, bump throughput.Dispatched
// In MarkFailedAsync:    after setting status, bump throughput.Failed
// In DeadLetterAsync:    after setting status, bump throughput.Failed

// New methods:
public ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct)
{
    var now = DateTimeOffset.UtcNow;
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

    dispatched = dispatched.OrderByDescending(v => v.DispatchedAt ?? v.CreatedAt).Take(dispatchedLimit).ToList();
    return ValueTask.FromResult(new OutboxSnapshot(pending, retry, dead, dispatched));
}

public async IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(
    TimeSpan window,
    [EnumeratorCancellation] CancellationToken ct)
{
    var cutoff = DateTimeOffset.UtcNow - window;
    foreach (var kv in _throughput.OrderBy(k => k.Key))
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
    return ValueTask.CompletedTask;
}

public ValueTask CancelAsync(Guid id, CancellationToken ct)
{
    if (!_entries.TryGetValue(id, out var entry))
        throw new InvalidOperationException($"Message {id} not found.");
    if (entry.Status == InMemoryEntryStatus.Succeeded || entry.Status == InMemoryEntryStatus.DeadLetter)
        throw new InvalidOperationException($"Message {id} cannot be cancelled.");
    _entries.TryRemove(id, out _);
    return ValueTask.CompletedTask;
}

public ValueTask ForceDispatchAsync(Guid id, CancellationToken ct)
{
    if (!_entries.TryGetValue(id, out var entry))
        throw new InvalidOperationException($"Message {id} not found.");
    if (entry.Status != InMemoryEntryStatus.Pending)
        throw new InvalidOperationException($"Message {id} is not pending.");
    entry.NextRetryAt = DateTimeOffset.UtcNow;
    return ValueTask.CompletedTask;
}

// Helpers:
private static OutboxMessageView ToView(InMemoryOutboxEntry e) => new()
{
    Id = e.Id,
    TypeName = e.TypeName,
    CreatedAt = e.CreatedAt,
    RetryCount = e.RetryCount,
    NextRetryAt = e.NextRetryAt,
    PayloadPreview = TryDecodePreview(e.Payload),
    DispatchedAt = e.Status == InMemoryEntryStatus.Succeeded ? DateTimeOffset.UtcNow : null,
    DeadLetterError = null,
};

private static string TryDecodePreview(byte[] payload)
{
    try { return System.Text.Encoding.UTF8.GetString(payload.Take(200).ToArray()); }
    catch { return $"<{payload.Length} bytes>"; }
}

private sealed class ThroughputAccumulator
{
    public int Dispatched;
    public int Failed;
}
```

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "InMemoryDashboardStore"`
Expected: 5/5 pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox.InMemory/InMemoryOutboxStore.cs tests/ZeroAlloc.Outbox.Tests/InMemoryDashboardStoreTests.cs
git commit -m "feat(outbox-inmemory): implement IOutboxDashboardStore"
```

---

### Task 5: Implement `IOutboxDashboardStore` on `EfCoreOutboxStore`

**Files:**
- Modify: `src/ZeroAlloc.Outbox.EfCore/EfCoreOutboxStore.cs`
- Test: `tests/ZeroAlloc.Outbox.Tests/EfCoreDashboardStoreTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.Outbox.Tests/EfCoreDashboardStoreTests.cs
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

public sealed class TestDbContext : DbContext
{
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
}

public class EfCoreDashboardStoreTests : IAsyncLifetime
{
    private TestDbContext _db = default!;
    private EfCoreOutboxStore _store = default!;

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite($"Data Source=:memory:;Cache=Shared;Mode=Memory")
            .Options;
        _db = new TestDbContext(opts);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();
        _store = new EfCoreOutboxStore(_db);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task GetSnapshotAsync_GroupsByStatus()
    {
        await _store.EnqueueAsync("T1", new byte[] { 1 }, null, default);
        await _store.EnqueueAsync("T2", new byte[] { 2 }, null, default);

        IOutboxDashboardStore dash = _store;
        var snapshot = await dash.GetSnapshotAsync(10, default);

        Assert.Equal(2, snapshot.Pending.Count);
    }

    [Fact]
    public async Task RequeueAsync_RestoresDeadLettered()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var id = (await _db.OutboxMessages.FirstAsync()).Id;
        await _store.DeadLetterAsync(id, "err", default);

        IOutboxDashboardStore dash = _store;
        await dash.RequeueAsync(id, default);

        var row = await _db.OutboxMessages.FindAsync(id);
        Assert.Equal(OutboxMessageStatus.Pending, row!.Status);
        Assert.Equal(0, row.RetryCount);
    }

    [Fact]
    public async Task CancelAsync_DeletesPending()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var id = (await _db.OutboxMessages.FirstAsync()).Id;

        IOutboxDashboardStore dash = _store;
        await dash.CancelAsync(id, default);

        Assert.Null(await _db.OutboxMessages.FindAsync(id));
    }

    [Fact]
    public async Task ForceDispatchAsync_UpdatesNextRetryAt()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var row = await _db.OutboxMessages.FirstAsync();
        row.NextRetryAt = DateTimeOffset.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();

        IOutboxDashboardStore dash = _store;
        await dash.ForceDispatchAsync(row.Id, default);

        var updated = await _db.OutboxMessages.FindAsync(row.Id);
        Assert.True(updated!.NextRetryAt <= DateTimeOffset.UtcNow);
    }
}
```

**Step 2: Run to verify fail**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "EfCoreDashboardStore"`
Expected: compilation error.

**Step 3: Implement**

Modify `EfCoreOutboxStore.cs` to implement `IOutboxDashboardStore`. Key points:

- Use `_db.OutboxMessages.AsNoTracking()` for reads in `GetSnapshotAsync`
- `GetSnapshotAsync` executes four parallel queries (or one with in-memory partition) — recommend one query + in-memory grouping to minimise round trips
- `GetThroughputAsync` groups `ProcessedAt` by minute, filters by `Status in (Succeeded, DeadLetter)` — project directly to `ThroughputPoint`
- `RequeueAsync`/`CancelAsync`/`ForceDispatchAsync` load by id, validate status, mutate, save

Example `GetSnapshotAsync`:

```csharp
public async ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct)
{
    var all = await _db.OutboxMessages.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    var now = DateTimeOffset.UtcNow;

    var pending = all.Where(e => e.Status == OutboxMessageStatus.Pending && e.RetryCount == 0)
        .Select(ToView).ToList();
    var retry = all.Where(e => e.Status == OutboxMessageStatus.Pending && e.RetryCount > 0)
        .Select(ToView).ToList();
    var dead = all.Where(e => e.Status == OutboxMessageStatus.DeadLetter)
        .Select(ToView).ToList();
    var dispatched = all.Where(e => e.Status == OutboxMessageStatus.Succeeded)
        .OrderByDescending(e => e.ProcessedAt ?? e.CreatedAt)
        .Take(dispatchedLimit)
        .Select(ToView).ToList();

    return new OutboxSnapshot(pending, retry, dead, dispatched);
}
```

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "EfCoreDashboardStore"`
Expected: 4/4 pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox.EfCore/EfCoreOutboxStore.cs tests/ZeroAlloc.Outbox.Tests/EfCoreDashboardStoreTests.cs
git commit -m "feat(outbox-efcore): implement IOutboxDashboardStore"
```

---

## Phase 3: Worker integration

### Task 6: Publish events from `OutboxWorkerService`

**Files:**
- Modify: `src/ZeroAlloc.Outbox/OutboxWorkerService.cs`
- Modify: `src/ZeroAlloc.Outbox/OutboxServiceCollectionExtensions.cs`
- Test: `tests/ZeroAlloc.Outbox.Tests/WorkerDashboardEventsTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.Outbox.Tests/WorkerDashboardEventsTests.cs
[Fact]
public async Task Worker_RaisesMessageDispatchedEvent_OnSuccess()
{
    // Use WebApplicationFactory / IHost with InMemoryOutboxStore, register a test dispatcher
    // that succeeds, enqueue a message, wait, assert MessageDispatchedEvent observed on the publisher's subscriber channel.
    // Concrete test setup details: follow existing OutboxWorkerServiceTests (if present).
}

[Fact]
public async Task Worker_RaisesMessageFailedEvent_OnTransientFailure() { /* similar */ }

[Fact]
public async Task Worker_RaisesMessageDeadLetteredEvent_OnExhaustedAttempts() { /* similar */ }
```

**Step 2: Run to verify fail**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "WorkerDashboardEvents"`
Expected: assertions fail — events not raised.

**Step 3: Implement**

Inject `IOutboxDashboardEventPublisher` into `OutboxWorkerService` (optional — resolve via `IServiceProvider.GetService<T>()` so dashboard is opt-in). After each `MarkSucceededAsync`, `MarkFailedAsync`, `DeadLetterAsync`, publish the corresponding event.

Register `ChannelOutboxDashboardEventPublisher` as a singleton in `OutboxServiceCollectionExtensions.AddOutbox()`.

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "WorkerDashboardEvents"`
Expected: 3/3 pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox/OutboxWorkerService.cs src/ZeroAlloc.Outbox/OutboxServiceCollectionExtensions.cs tests/ZeroAlloc.Outbox.Tests/WorkerDashboardEventsTests.cs
git commit -m "feat(outbox): publish dashboard events from worker service"
```

---

## Phase 4: Dashboard package scaffold

### Task 7: Create `ZeroAlloc.Outbox.Dashboard` project

**Files:**
- Create: `src/ZeroAlloc.Outbox.Dashboard/ZeroAlloc.Outbox.Dashboard.csproj`
- Create: `src/ZeroAlloc.Outbox.Dashboard/OutboxDashboardEndpoints.cs`
- Create: `src/ZeroAlloc.Outbox.Dashboard/OutboxDashboardServiceCollectionExtensions.cs`
- Modify: `ZeroAlloc.Outbox.slnx`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.Outbox.Tests/DashboardEndpointsTests.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class DashboardEndpointsTests
{
    [Fact]
    public async Task MapOutboxDashboard_RegistersExpectedRoutes()
    {
        var app = WebApplication.Create();
        app.Services.GetRequiredService<IServiceCollection>(); // smoke
        app.MapOutboxDashboard("/outbox");
        // Assert at least one endpoint exists under /outbox
        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        Assert.Contains(dataSource.Endpoints, e => e.DisplayName?.Contains("/outbox", StringComparison.OrdinalIgnoreCase) == true);
    }
}
```

**Step 2: Run to verify fail**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "DashboardEndpoints"`
Expected: `MapOutboxDashboard` not found.

**Step 3: Implement**

Create the csproj:

```xml
<!-- src/ZeroAlloc.Outbox.Dashboard/ZeroAlloc.Outbox.Dashboard.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <PackageId>ZeroAlloc.Outbox.Dashboard</PackageId>
    <Description>Operations dashboard for ZeroAlloc.Outbox. Minimal API + SSE + embedded HTML.</Description>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\ZeroAlloc.Outbox\ZeroAlloc.Outbox.csproj" />
  </ItemGroup>
</Project>
```

Create a minimal endpoints stub that registers `GET /outbox` returning `200 OK`. Add the project to `ZeroAlloc.Outbox.slnx`.

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.Outbox.Tests --filter "DashboardEndpoints"`
Expected: pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox.Dashboard tests/ZeroAlloc.Outbox.Tests/DashboardEndpointsTests.cs ZeroAlloc.Outbox.slnx
git commit -m "feat(outbox-dashboard): scaffold dashboard project with MapOutboxDashboard stub"
```

---

### Task 8: Implement snapshot endpoint

**Files:**
- Modify: `src/ZeroAlloc.Outbox.Dashboard/OutboxDashboardEndpoints.cs`
- Test: `tests/ZeroAlloc.Outbox.Tests/SnapshotEndpointTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task Snapshot_ReturnsOutboxState()
{
    await using var factory = new WebApplicationFactory<Program>();
    var client = factory.CreateClient();

    var response = await client.GetAsync("/outbox/api/snapshot");
    var snapshot = await response.Content.ReadFromJsonAsync<OutboxSnapshot>();

    Assert.NotNull(snapshot);
    Assert.NotNull(snapshot.Pending);
}
```

**Step 2: Run to verify fail**

Run: `dotnet test --filter "SnapshotEndpoint"`
Expected: 404 or deserialization fails.

**Step 3: Implement**

```csharp
routes.MapGet($"{basePath}/api/snapshot",
    async (IOutboxDashboardStore store, CancellationToken ct) =>
        Results.Ok(await store.GetSnapshotAsync(dispatchedLimit: 100, ct)));
```

**Step 4: Run tests**

Expected: pass.

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Outbox.Dashboard/OutboxDashboardEndpoints.cs tests/ZeroAlloc.Outbox.Tests/SnapshotEndpointTests.cs
git commit -m "feat(outbox-dashboard): add /api/snapshot endpoint"
```

---

### Task 9: Implement SSE events endpoint

**Files:**
- Modify: `src/ZeroAlloc.Outbox.Dashboard/OutboxDashboardEndpoints.cs`
- Test: `tests/ZeroAlloc.Outbox.Tests/SseEventsEndpointTests.cs`

**Step 1: Write the failing test**

Open a streamed response, publish an event via `IOutboxDashboardEventPublisher`, assert the client observes a `data:` line with the JSON payload, and an `event:` line with the event type.

**Step 2: Run to verify fail**

**Step 3: Implement**

```csharp
routes.MapGet($"{basePath}/api/events",
    async (HttpContext ctx, IOutboxDashboardEventPublisher pub, CancellationToken ct) =>
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var reader = pub.Subscribe();
        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            var name = evt.GetType().Name;
            var json = JsonSerializer.Serialize(evt, evt.GetType());
            await ctx.Response.WriteAsync($"event: {name}\ndata: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    });
```

**Step 4: Run tests**

**Step 5: Commit**

```bash
git commit -m "feat(outbox-dashboard): add /api/events SSE endpoint"
```

---

### Task 10: Implement throughput endpoint

Similar TDD pattern. Endpoint:

```csharp
routes.MapGet($"{basePath}/api/throughput",
    async (IOutboxDashboardStore store, TimeSpan? window, CancellationToken ct) =>
    {
        var points = new List<ThroughputPoint>();
        await foreach (var p in store.GetThroughputAsync(window ?? TimeSpan.FromHours(1), ct))
            points.Add(p);
        return Results.Ok(points);
    });
```

Commit: `feat(outbox-dashboard): add /api/throughput endpoint`

---

### Task 11: Implement write action endpoints

Three endpoints (`requeue`, `cancel`, `force-dispatch`). Each:

1. Calls the corresponding `IOutboxDashboardStore` method
2. Catches `InvalidOperationException` → `422 Unprocessable Entity`
3. Publishes a `MessageRequeuedEvent` / `MessageCancelledEvent` on success (force-dispatch events come from the worker)

```csharp
routes.MapPost($"{basePath}/api/messages/{{id:guid}}/requeue",
    async (Guid id, IOutboxDashboardStore store, IOutboxDashboardEventPublisher pub, CancellationToken ct) =>
    {
        try
        {
            await store.RequeueAsync(id, ct);
            await pub.PublishAsync(new MessageRequeuedEvent(id, DateTimeOffset.UtcNow), ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex) { return Results.UnprocessableEntity(new { error = ex.Message }); }
    });
```

Mirror for `cancel` and `force-dispatch`. Add integration tests for each including failure paths (422 for invalid state).

Commit: `feat(outbox-dashboard): add write action endpoints (requeue, cancel, force-dispatch)`

---

## Phase 5: HTML dashboard

### Task 12: Embed HTML dashboard as resource

**Files:**
- Create: `src/ZeroAlloc.Outbox.Dashboard/wwwroot/outbox.html`
- Create: `src/ZeroAlloc.Outbox.Dashboard/wwwroot/outbox.css`
- Create: `src/ZeroAlloc.Outbox.Dashboard/wwwroot/outbox.js`
- Modify: `src/ZeroAlloc.Outbox.Dashboard/ZeroAlloc.Outbox.Dashboard.csproj` (embed as resource)
- Modify: `src/ZeroAlloc.Outbox.Dashboard/OutboxDashboardEndpoints.cs`

The HTML page has:
- Summary bar (live counts + SSE indicator)
- Throughput chart placeholder (SVG, rendered in JS)
- Four tabs (Pending / Retry Queue / Dead-lettered / Dispatched)

The JS:
1. On load: `fetch('/outbox/api/snapshot')` → populate tabs
2. Connects EventSource to `/outbox/api/events`
3. On each event, patch local state and re-render affected tab
4. Action buttons: `POST` to respective endpoint, optimistic UI update

Embed as resources via `<EmbeddedResource Include="wwwroot/*" />`. `GET /outbox` returns the HTML with a `<base href>` matching `basePath`.

Commit: `feat(outbox-dashboard): embed HTML dashboard with SSE-driven updates`

---

### Task 13: Add SVG throughput chart

Vanilla JS renders a line chart from the `/api/throughput` response. Updates live as `MessageDispatched` / `MessageFailed` events arrive (bucketed client-side by minute).

Commit: `feat(outbox-dashboard): add SVG throughput chart`

---

## Phase 6: Blazor component

### Task 14: Create `ZeroAlloc.Outbox.Dashboard.Blazor` project

**Files:**
- Create: `src/ZeroAlloc.Outbox.Dashboard.Blazor/ZeroAlloc.Outbox.Dashboard.Blazor.csproj`
- Create: `src/ZeroAlloc.Outbox.Dashboard.Blazor/OutboxDashboard.razor`
- Create: `src/ZeroAlloc.Outbox.Dashboard.Blazor/OutboxDashboard.razor.cs`
- Create: `src/ZeroAlloc.Outbox.Dashboard.Blazor/OutboxDashboardServiceCollectionExtensions.cs`

The component calls the REST/SSE endpoints of `MapOutboxDashboard` via `HttpClient` (injected `IHttpClientFactory` with a named client). Parameters:

```csharp
[Parameter] public string BaseUrl { get; set; } = "/outbox";
```

Commit: `feat(outbox-dashboard-blazor): add OutboxDashboard Blazor component`

---

## Phase 7: Integration tests and polish

### Task 15: End-to-end integration test

Write one full-loop test per store adapter:
1. Enqueue message
2. Open SSE subscription
3. Wait for worker to dispatch (configure test dispatcher that succeeds)
4. Assert `MessageDispatchedEvent` observed on SSE stream
5. Requeue (after forcing a dead-letter), observe `MessageRequeuedEvent`

Commit: `test(outbox-dashboard): add end-to-end integration tests for InMemory and EfCore`

---

### Task 16: Update README and package metadata

- Update `README.md` with `MapOutboxDashboard` usage example
- Add `PackageTags` to dashboard csproj: `outbox;dashboard;observability;zeroalloc`
- Ensure both new projects are listed in the CI pack loop

Commit: `docs(outbox): document dashboard usage`

---

## Execution notes

- **Worktree:** before starting, create an isolated worktree for this work via `superpowers:using-git-worktrees`.
- **Analyzer settings:** this repo uses `TreatWarningsAsErrors=true`. New code must clear the analyzers configured in `Directory.Build.props` without suppressions except where pre-existing patterns already do so.
- **Test framework:** xUnit. Integration tests use `WebApplicationFactory<Program>` — check `tests/ZeroAlloc.Outbox.Tests/` for the existing pattern before writing new ones.
- **Commit style:** conventional commits (`feat(outbox): ...`, `test(outbox-efcore): ...`, `docs: ...`). Keep commits under ~200 lines where possible.
- **TDD discipline:** each task has Write test → Fail → Implement → Pass → Commit. Do not skip the fail step.
