# ZeroAlloc.Outbox.Dashboard — Regression Report

## Summary

| Metric | Value |
|---|---|
| Date | 2026-04-22 08:46 |
| Application URL | `http://localhost:5123/outbox/` (via `http://host.docker.internal:5123/outbox/` from Playwright container) |
| Host | `samples/ZeroAlloc.Outbox.DashboardSample` (new, this session) |
| Pages Tested | 1 SPA-style dashboard with 4 tab views |
| Viewports Tested | 3 (Desktop 1920x1080, Tablet 768x1024, Mobile 375x812) |
| Existing Tests Passed | 79 / 79 |
| Existing Tests Failed | 0 |
| Console Errors Found | 1 (favicon 404 — benign) |
| Network Errors Found | 0 |
| Visual Issues Found | 5 |
| Overall Status | **WARN** (functional, but mobile responsiveness and chart rendering need work before shipping externally) |

---

## Phase 1: Discovery

- Test framework: xUnit v2
- Test projects:
  - [tests/ZeroAlloc.Outbox.Tests/](tests/ZeroAlloc.Outbox.Tests/)
  - [tests/ZeroAlloc.Outbox.Generator.Tests/](tests/ZeroAlloc.Outbox.Generator.Tests/)
- Host for browser testing: none existed; created [samples/ZeroAlloc.Outbox.DashboardSample/](samples/ZeroAlloc.Outbox.DashboardSample/) with seeded fixture data (3 pending, 2 retry, 2 dead-lettered, 5 dispatched).

---

## Phase 2: Existing Test Results

| Framework | Command | Total | Passed | Failed | Skipped |
|---|---|---|---|---|---|
| xUnit v2 | `dotnet test --nologo --verbosity quiet` | 79 | 79 | 0 | 0 |

- `ZeroAlloc.Outbox.Tests`: 72 / 72 passing
- `ZeroAlloc.Outbox.Generator.Tests`: 7 / 7 passing
- Build clean across `net8.0`, `net9.0`, `net10.0` with `TreatWarningsAsErrors=true` and the full analyzer stack (Meziantou, Roslynator, ErrorProne, ZeroAlloc, NetFabric.Hyperlinq).

---

## Phase 3: Browser-Based Testing

### 3a. Setup & Authentication

- Navigated to `http://host.docker.internal:5123/outbox/` → `GET /outbox/` returned `200 OK, text/html`.
- **No login form detected.** Dashboard is mounted unauthenticated in the sample app (as documented in the README; production apps must add `RequireAuthorization`).
- CSP header present: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'`.

### 3b. Functional Checks

| Check | Result |
|---|---|
| Page title | ✓ `Outbox Dashboard` |
| Initial snapshot (`GET /api/snapshot`) | ✓ `200 OK`, counts `pending=3, retry=2, dead=2, dispatched=5` (match seed) |
| Throughput fetch (`GET /api/throughput?windowMinutes=60`) | ✓ `200 OK` |
| SSE connection (`GET /api/events`) | ✓ established (see Issue M1 about delayed `onopen`) |
| Tab switching: Pending → Retry → Dead-lettered → Dispatched | ✓ all four render correctly |
| Dead-lettered tab has extra `Error` column | ✓ |
| Dispatched tab has no action buttons | ✓ |
| Action buttons rendered per-tab | ✓ Pending/Retry show `force-dispatch cancel`; Dead show `requeue cancel`; Dispatched none |
| Payload preview truncated correctly | ✓ |
| Console errors | 1 × favicon 404 — benign; no JS runtime errors |
| Network failures (non-200) | 0 (ignoring favicon) |

### 3c. Visual Evaluation

Screenshots were captured via Playwright MCP but saved only to the Playwright container's temp volume (`/tmp/playwright-output/`), which is not accessible from the host. Findings below are based on the embedded tool-response images inspected during the session.

#### Desktop 1920 × 1080

**Per-tab observations:**

- **Pending tab** — 3 rows render correctly. Layout usable but sparse (~4/5 of viewport is empty below the table).
- **Retry Queue tab** — 2 rows, retry count = 2 displayed correctly.
- **Dead-lettered tab** — 2 rows, `Error` column added, `requeue` + `cancel` buttons visible, payload preview and error message both render.
- **Dispatched tab** — 5 rows, ordered most-recent-first (notif-4 → notif-0), no action buttons.

**Visual issues (Desktop):**

1. **[Major]** Throughput chart SVG scales to full container width (~1900px) while its `viewBox` is `0 0 600 100`. Text labels (`dispatched (max 5)` / `failed`) become pixelated / squiggly at ~3.2× scale. The chart legend looks broken despite being functionally correct.
2. **[Major]** No polyline rendered — all seeded mutations happened within a single minute bucket so the chart has one data point. A `<polyline>` needs ≥ 2 points to draw a visible line. With only one point it's effectively invisible.
3. **[Minor]** Large empty vertical space below 3-row tables.

#### Tablet 768 × 1024

- Layout remains usable. Chart legend is now readable because the SVG doesn't scale up as aggressively.
- All tabs render within viewport width; table columns do not overflow.
- Dead-lettered tab: buttons naturally stack vertically (`requeue` above `cancel`) when the row gets tight — graceful wrapping, no style changes needed.

**Visual issues (Tablet):**

- Same two chart issues as desktop (Major 1 and 2 above) persist at smaller scale but are much less noticeable.

#### Mobile 375 × 812

- **[Critical]** **Horizontal overflow**. Table forces the page width to ~610 px, so the user must scroll horizontally to see action buttons. The `<base>` layout has no responsive rule forcing the table into a horizontally-scrollable container.
- **[Critical]** Chart legend labels (`dispatched (max 5)` / `failed`) render but are truncated at the SVG edge because the SVG retains its 600px-wide intrinsic size relative to a 375px viewport.
- **[Major]** Summary bar fits by luck (`Pending: 3 Retry: 2 Dead: 2 ● live`), but longer values would push it out of bounds. No flex-wrap on narrow viewports.
- **[Major]** Action buttons are small enough to be below the 44×44 CSS-px touch target recommended by WCAG 2.5.5.

---

## Functional Findings

| Id | Severity | Description |
|---|---|---|
| F1 | Minor | **SSE indicator delayed ~15 s.** `EventSource.onopen` does not fire until the server's first body byte reaches the client, which in Kestrel (default) only happens after the first `WriteAsync`. Because the SSE endpoint writes nothing until an event arrives, the client sees the stale `● connecting` indicator until the first 15-second heartbeat. Fix: call `await ctx.Response.StartAsync(ct)` in `StreamEventsAsync` before entering the read loop, or write an initial `: ready\n\n` comment frame. |
| F2 | Minor | **Favicon 404.** `GET /favicon.ico` returns 404. Either ship a 1-byte inline favicon in the embedded HTML (`<link rel="icon" href="data:,">`) or add a no-op endpoint. |
| F3 | Minor | **Payload preview shows culture-formatted doubles.** The seeded sample interpolates `{42.50 + i}` which becomes `42,5` in nl-NL culture. Not a dashboard bug per se — the dashboard correctly escapes + renders what the store hands it. Flagging because consumers with mixed-locale payloads may see inconsistent previews. Consider invariant-culture formatting at the `PayloadPreview` derivation site. |

---

## Visual Issues

| Id | Severity | Viewport(s) | Description |
|---|---|---|---|
| V1 | Critical | Mobile | Horizontal overflow on mobile. Table width (~610 px) exceeds viewport (375 px). Fix: wrap tables in a `.table-wrap { overflow-x: auto; }` container, or use `@media (max-width: 480px)` to collapse less-critical columns (Id, Created). |
| V2 | Critical | Mobile | Chart legend text clipped at right edge (`dispatched (max 5)` overflows). Fix: set `#throughput-chart { max-width: 100%; height: auto; }` and/or reduce `viewBox` padding. |
| V3 | Major | Desktop, Tablet | SVG chart labels render pixelated/squiggly at large scales because the chart stretches to container width while keeping `viewBox="0 0 600 100"`. Fix: cap chart container width (`max-width: 800px`) or use absolute `<text font-size>` that doesn't get scaled. |
| V4 | Major | All viewports | Single data-point case renders as invisible polyline. Fix: when `points.length === 1`, emit a `<circle>` marker instead of a `<polyline>`. Also show a "No data yet" fallback when all points are zero. |
| V5 | Major | Mobile | Action buttons below 44×44 px touch target. Fix: `@media (pointer: coarse) { .tab-panel button { min-height: 44px; min-width: 44px; padding: 10px 14px; } }` |

---

## Recommendations

### Critical (do before any public deploy)

1. **Fix mobile overflow (V1).** Add responsive CSS: wrap tables in horizontally-scrollable containers, or collapse columns below a breakpoint. The dashboard in its current form is effectively unusable on phones.
2. **Fix chart clipping (V2).** Constrain the SVG so it never exceeds its container, and ensure the labels stay inside the viewBox on narrow widths.

### Major (do before wider adoption)

3. **Chart at large scales (V3).** Cap chart container width at ~800 px; labels remain legible.
4. **Single-point chart fallback (V4).** Render a dot marker for one point; show placeholder text for zero-data.
5. **Touch targets (V5).** Minimum 44×44 px on coarse pointers.

### Minor / follow-ups

6. **SSE `onopen` delay (F1).** `Response.StartAsync(ct)` in `StreamEventsAsync` before the loop, OR emit an initial ready frame. Fifteen seconds of `● connecting` looks broken to a first-time user.
7. **Favicon 404 (F2).** Inline `<link rel="icon" href="data:,">` to silence the log entry.
8. **Payload preview culture (F3).** Consumer-side concern; worth a doc note.

### Suggestions (polish)

9. **Surface the polling interval and SSE heartbeat cadence in the dashboard footer** ("auto-refreshing · last update 2s ago") so operators can tell the view is live without having to stare at the indicator.
10. **Add a "Copy ID" button** to each row. Ops will want full Guids in logs / DB queries; currently only the truncated 8-char prefix is shown (full Id in `title` tooltip).
11. **Consider a "refresh now" button** alongside the SSE indicator for when users don't trust the live view.

---

## Conversation Summary

- **Overall status:** WARN
- **Issue counts:** 2 critical, 3 major, 3 minor, 3 suggestions
- **Top 3 findings:**
  1. **Mobile is unusable (V1+V2).** Horizontal overflow forces scrolling and clips the chart. Any consumer mounting this dashboard and opening it on a phone will immediately see broken-looking layout. Must fix before ship.
  2. **Throughput chart looks broken at desktop scale (V3+V4).** The labels render pixelated due to SVG up-scaling, and the single-point-data case renders as nothing — a fresh install will show an empty chart next to legends, which reads as a bug.
  3. **SSE `● connecting` indicator sticks for ~15 s (F1).** Not a bug, but looks like one: `onopen` doesn't fire until Kestrel flushes, which is the first heartbeat. First-run UX feels broken.
- **Report path:** `docs/regression-report-2026-04-22-0846.md`

---

## Notes on this test run

- The Playwright MCP server's browser runs in a container; screenshots were written to a container-local path (`/tmp/playwright-output/`) that the host can't access. Visual findings above were captured by inspecting the tool-response embedded images, not by saving artifacts. A future test run should persist screenshots via a volume mount if durable evidence is required.
- The sample app ([samples/ZeroAlloc.Outbox.DashboardSample/](samples/ZeroAlloc.Outbox.DashboardSample/)) is committed to the repo and can be re-run with `dotnet run --project samples/ZeroAlloc.Outbox.DashboardSample --urls http://localhost:5123`.
