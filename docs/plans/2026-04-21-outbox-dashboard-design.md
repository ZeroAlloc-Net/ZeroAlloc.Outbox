# ZeroAlloc.Outbox.Dashboard — Design

**Goal:** Hangfire-style operations dashboard for `ZeroAlloc.Outbox` — visibility into outbox message state via a single endpoint served from the host application.

**Tech stack:** ASP.NET Core Minimal API, Server-Sent Events (SSE), vanilla JS (HTML dashboard), Blazor (optional component).

---

## Architecture

Two NuGet packages, same repo as `ZeroAlloc.Outbox`:

| Package | Contents |
|---|---|
| `ZeroAlloc.Outbox` | Adds `IOutboxDashboardStore` interface (core owns the data). |
| `ZeroAlloc.Outbox.Dashboard` | REST API, SSE endpoint, HTML dashboard, Blazor component. |

Each existing store adapter (`InMemory`, `EfCore`, `Redis`) implements `IOutboxDashboardStore` alongside `IOutboxStore` — no extra registration.

### Wiring

```csharp
app.MapOutboxDashboard("/outbox");                    // unauthenticated
app.MapOutboxDashboard("/outbox").RequireAuthorization();  // protected

builder.Services.AddOutboxDashboardBlazor();          // opt-in Blazor
// <OutboxDashboard BaseUrl="/outbox" />
```

The HTML dashboard is self-contained (embedded resources) — no Blazor dependency on the server. The Blazor component is a separate opt-in surface that renders the same data via the REST/SSE endpoints.

---

## `IOutboxDashboardStore`

```csharp
public interface IOutboxDashboardStore
{
    Task<OutboxSnapshot> GetSnapshotAsync(CancellationToken ct);
    IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(TimeSpan window, CancellationToken ct);

    Task RequeueAsync(Guid id, CancellationToken ct);
    Task CancelAsync(Guid id, CancellationToken ct);
    Task ForceDispatchAsync(Guid id, CancellationToken ct);
}

public sealed record OutboxSnapshot(
    IReadOnlyList<OutboxMessage> Pending,
    IReadOnlyList<OutboxMessage> RetryQueue,
    IReadOnlyList<OutboxMessage> DeadLettered,
    IReadOnlyList<OutboxMessage> Dispatched);   // most recent N

public sealed record ThroughputPoint(DateTimeOffset Bucket, int Dispatched, int Failed);
```

The snapshot returns all four lists in a single round trip. `Dispatched` is capped at a configurable N (default 100 most recent) — the full history is never loaded.

---

## REST API

| Method | Path | Description |
|---|---|---|
| `GET` | `/outbox` | HTML dashboard (embedded resource). |
| `GET` | `/outbox/api/snapshot` | Full state for initial client load. |
| `GET` | `/outbox/api/events` | SSE stream of domain events. |
| `GET` | `/outbox/api/throughput?window=1h` | Throughput buckets for chart. |
| `POST` | `/outbox/api/messages/{id}/requeue` | Requeue dead-lettered message. |
| `POST` | `/outbox/api/messages/{id}/cancel` | Cancel pending or retry-queue message. |
| `POST` | `/outbox/api/messages/{id}/force-dispatch` | Dispatch immediately. |

All routes are registered under the path passed to `MapOutboxDashboard(path)` — the default is `/outbox`.

---

## SSE event model

Six typed events pushed over the stream. The client applies them to the local state derived from the initial snapshot.

```csharp
public sealed record MessageQueued(Guid Id, string MessageType, DateTimeOffset QueuedAt);
public sealed record MessageDispatched(Guid Id, DateTimeOffset DispatchedAt, int AttemptCount);
public sealed record MessageFailed(Guid Id, string Error, int AttemptCount, DateTimeOffset NextRetryAt);
public sealed record MessageDeadLettered(Guid Id, string Error, int TotalAttempts);
public sealed record MessageRequeued(Guid Id, DateTimeOffset RequeuedAt);
public sealed record MessageCancelled(Guid Id);
```

### Wire format

```
event: MessageDispatched
data: {"id":"...","dispatchedAt":"2026-04-21T12:00:00Z","attemptCount":1}
```

### Event publication

The outbox core raises these events whenever message state changes — dispatch loop, requeue action, cancel action. `IOutboxDashboardStore` is the publication point; implementations push events into an in-process `Channel<OutboxDashboardEvent>`. The SSE endpoint subscribes to the channel per connected client.

### Throughput

The chart is a client-side rollup of `MessageDispatched` and `MessageFailed` events. `/api/throughput` seeds the chart on initial load; SSE keeps it live from there — no separate throughput stream.

---

## Write actions

| Action | Valid on | Effect | Failure |
|---|---|---|---|
| **Requeue** | Dead-lettered | Move to pending, reset attempt count, raise `MessageRequeued`. | `422` if message is not dead-lettered. |
| **Cancel** | Pending, Retry queue | Remove from outbox, raise `MessageCancelled`. | `422` if message is dispatched or dead-lettered. |
| **Force-dispatch** | Pending | Atomic claim via existing store mechanism, dispatch now, raise `MessageDispatched` or `MessageFailed`. | `409` if polling loop claimed first; `422` if not pending. |

Force-dispatch reuses the store's atomic claim (`SELECT ... FOR UPDATE SKIP LOCKED` in EF Core, `WATCH`/`MULTI` in Redis). No double-dispatch possible — concurrent claim from the polling loop loses cleanly with a `409 Conflict`.

The dashboard disables invalid-state buttons client-side; the API enforces invariants regardless.

---

## UI

### HTML dashboard

```
┌─────────────────────────────────────────────────────────┐
│ Outbox │ Pending: 3 │ Retry: 1 │ Dead: 2 │ ● live        │
├─────────────────────────────────────────────────────────┤
│ [Throughput chart — dispatched/min, last 60 min]         │
├──────────────┬──────────────┬──────────────┬────────────┤
│   Pending    │ Retry Queue  │ Dead-lettered│ Dispatched │
└──────────────┴──────────────┴──────────────┴────────────┘
```

- **Summary bar** — live counts + SSE connection indicator (green: connected, amber: reconnecting, red: disconnected).
- **Throughput chart** — SVG rendered in vanilla JS, no external library. Switchable 1h / 24h window.
- **Tabs** — each shows message type, payload preview, age, attempt count, and context-appropriate action buttons.

### Action buttons per tab

| Tab | Actions |
|---|---|
| Pending | Force-dispatch, Cancel |
| Retry queue | Force-dispatch, Cancel |
| Dead-lettered | Requeue, Cancel |
| Dispatched | — (read-only) |

### Blazor component

`<OutboxDashboard BaseUrl="/outbox" />` — same visual structure as the HTML dashboard, same REST/SSE endpoints. No duplication of server logic; the component is pure UI.

---

## Error handling

- **SSE reconnection** — client retries with exponential backoff. On reconnect, the client re-fetches `/api/snapshot` to re-establish ground truth (any events missed during disconnect are reconciled).
- **Invalid state transitions** — `422 Unprocessable Entity` with a typed error body.
- **Concurrent force-dispatch vs. polling loop** — atomic claim at the store level guarantees one winner; loser receives `409 Conflict`.
- **Channel backpressure** — if a client falls behind, the server drops old events for that client and the client re-fetches a snapshot (same recovery path as SSE reconnect).

---

## Testing strategy

- **Integration tests** per store adapter — end-to-end: write message → SSE event observed → requeue → requeue event observed.
- **API contract tests** — assert REST responses match expected shapes for all endpoints.
- **Force-dispatch concurrency test** — polling loop + force-dispatch racing for the same message; assert exactly one dispatches.
- **SSE reconnect test** — disconnect mid-stream, reconnect, assert state converges.
- **HTML dashboard smoke test** — load the page, confirm snapshot renders, confirm SSE connects.

The Blazor component re-uses the HTML dashboard's integration tests — it talks to the same REST/SSE endpoints.

---

## Dependencies

- `ZeroAlloc.Outbox` (core) — required.
- No Blazor or Minimal API dependency in core.
- `Microsoft.AspNetCore.App` framework reference in `ZeroAlloc.Outbox.Dashboard` — required for Minimal API and SSE.
- `Microsoft.AspNetCore.Components.Web` — only in the Blazor component project, optional consumer install.
