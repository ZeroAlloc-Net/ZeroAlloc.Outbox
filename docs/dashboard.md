---
id: dashboard
title: Dashboard
sidebar_position: 12
---

# Dashboard

Operate the outbox at runtime: inspect pending, retry, dead-lettered, and dispatched messages; watch a live throughput chart; and requeue, cancel, or force-dispatch individual messages.

## Install

```bash
dotnet add package ZeroAlloc.Outbox.Dashboard
```

## Wire up

```csharp
// Register the SSE event publisher (required for live updates).
builder.Services.AddOutbox().WithDashboardEvents();

// Map the dashboard under any prefix.
app.MapOutboxDashboard("/outbox");
```

The mapped root (`/outbox`) serves the HTML dashboard. REST endpoints (`snapshot`, `throughput`, `requeue`, `cancel`, `force-dispatch`) and the SSE stream (`events`) live under the same prefix.

## What you see

The dashboard is single-page with four tabs, a live badge ribbon, and a throughput chart that updates from a Server-Sent Events stream.

### Pending

Messages awaiting their first dispatch attempt. Each row shows the message type, the generated ID, enqueue time, attempt counter (always `0/N` on this tab), and the JSON payload. `Force dispatch` runs the message immediately; `Cancel` removes it from the queue.

![Pending tab — desktop](screenshots/pending-desktop.png)

### Retry

Messages that failed at least once and are scheduled for a retry. The `Retry` column reads `n/max`, and the `Next attempt` column shows the back-off target derived from `OutboxOptions.RetryBaseDelay`.

![Retry tab — desktop](screenshots/retry-desktop.png)

### Dead-lettered

Messages that exhausted `MaxAttempts`. The `Error` column carries the last failure reason captured when the worker moved the entry to dead-letter. `Requeue` re-enqueues the message with a fresh attempt counter.

![Dead-lettered tab — desktop](screenshots/dead-desktop.png)

### Dispatched

Recently-succeeded messages — the same series that drives the throughput chart at the top of the page. This is a bounded history window; the store decides the retention.

![Dispatched tab — desktop](screenshots/dispatched-desktop.png)

## Responsive layout

The dashboard is a plain HTML/JS page with no framework — it reflows cleanly to tablet and mobile viewports. Tablet (768 × 1024) and mobile (375 × 812) captures live alongside the desktop ones in [`docs/screenshots/`](screenshots/).

## Security

All write actions are `POST` endpoints:

- `POST /outbox/api/messages/{id}/requeue`
- `POST /outbox/api/messages/{id}/cancel`
- `POST /outbox/api/messages/{id}/force-dispatch`

**Never mount the dashboard unauthenticated in production.** The `IEndpointConventionBuilder` returned by `MapOutboxDashboard` accepts any standard ASP.NET Core auth middleware:

```csharp
app.MapOutboxDashboard("/outbox").RequireAuthorization("AdminPolicy");
```

CSRF protection is the host's responsibility — the dashboard neither emits nor validates anti-forgery tokens. For cookie-based auth schemes, enable `[ValidateAntiForgeryToken]` or the antiforgery middleware.

## Blazor component

For apps already using Blazor, `ZeroAlloc.Outbox.Dashboard.Blazor` ships an `<OutboxDashboard />` component that embeds the dashboard via `iframe`:

```bash
dotnet add package ZeroAlloc.Outbox.Dashboard.Blazor
```

```razor
@* In any Razor page / component *@
<OutboxDashboard BaseUrl="/outbox" />
```

You still need `MapOutboxDashboard("/outbox")` on the host — the Blazor component is a thin wrapper around the mapped endpoints.

## Sample

A fully-seeded sample host lives at [`samples/ZeroAlloc.Outbox.DashboardSample`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/tree/main/samples/ZeroAlloc.Outbox.DashboardSample). It registers `ZeroAlloc.Outbox.InMemory`, seeds messages across every state (pending, retry, dead-letter, dispatched), and strips out `OutboxWorkerService` so the seeded state stays stable while you click through tabs. Run it with:

```bash
dotnet run --project samples/ZeroAlloc.Outbox.DashboardSample
```

and open `http://localhost:5123/outbox/`.
