# ZeroAlloc.Outbox Infrastructure & Documentation Design

## Goal

Bring ZeroAlloc.Outbox to full parity with ZeroAlloc.Mediator: CI/CD pipelines, automated release management, dependency updates, commit linting, GitHub templates, an updated README, and a full Mediator-style documentation set adapted to the outbox domain.

## Architecture

Three independent concerns implemented together: infrastructure (GitHub Actions + config files at repo root), documentation (full docs/ tree), and README (badges + quick-start + features). All patterns are taken directly from ZeroAlloc.Mediator and ZeroAlloc.Resilience, adapted for the four Outbox packages.

---

## Section 1: Infrastructure

### Root config files

| File | Pattern | Notes |
|---|---|---|
| `GitVersion.yml` | Mediator (ContinuousDeployment) | `main` → alpha label; `release/*` → rc label |
| `release-please-config.json` | Resilience (with changelog sections) | feat/fix/perf/refactor/docs/test/build/ci/chore sections |
| `.release-please-manifest.json` | Seeded at `0.1.0` | Auto-updated by release-please after first release |
| `renovate.json` | Mediator | Monday 6am Amsterdam; group analyzers/xunit/Microsoft.Extensions/GitHub Actions; automerge patches |
| `.commitlintrc.yml` | Mediator | Scopes: `core`, `generator`, `efcore`, `inmemory`, `docs`, `ci`, `deps` |
| `LICENSE` | MIT | Deferred — addressed in a separate cross-repo sweep |
| `global.json` | SDK `10.0.x` | Pin .NET SDK version |
| `.config/dotnet-tools.json` | Mediator | `dotnet-gitversion` tool manifest |

### GitHub workflows (`.github/workflows/`)

**`ci.yml`** — triggered on push to `main`/`release-please--**` and PRs to `main`:
1. Checkout (`fetch-depth: 0` for GitVersion)
2. Setup .NET 10.0.x
3. `dotnet tool restore`
4. GitVersion → extract `SemVer`
5. `dotnet restore`
6. `dotnet build -c Release -p:Version=<semver>`
7. `dotnet test -c Release --no-build`
8. `dotnet pack` × 4 packages: `ZeroAlloc.Outbox`, `ZeroAlloc.Outbox.Generator`, `ZeroAlloc.Outbox.EfCore`, `ZeroAlloc.Outbox.InMemory`
9. Push pre-release to NuGet on `main` push only (`--skip-duplicate`)

**`release-please.yml`** — triggered on push to `main`:
- Job 1 (`release-please`): run `googleapis/release-please-action@v4`, output `release_created`, `tag_name`, `version`
- Job 2 (`publish`): conditional on `release_created` → build → test → pack × 4 → push to NuGet → upload to GitHub Release

**`release.yml`** — triggered on GitHub Release published:
- Extract version from tag → build → test → pack × 4 → push to NuGet

**`trigger-website.yml`** — triggered on push to `main` affecting `docs/**`:
- Dispatch `repository_dispatch` to `ZeroAlloc.Website` repo

### GitHub templates (`.github/`)

- `PULL_REQUEST_TEMPLATE.md` — Summary, Type of Change (feat/fix/perf/refactor/docs/test/build/ci/chore), Changes, Breaking Changes, Test Plan, Checklist
- `ISSUE_TEMPLATE/bug_report.yml` — Form-based bug report
- `ISSUE_TEMPLATE/feature_request.yml` — Form-based feature request

---

## Section 2: Documentation structure

Full Mediator-style depth, adapted for the outbox domain:

```
docs/
  index.md                    # Table of contents + one-paragraph overview
  getting-started.md          # Expand existing file: frontmatter + badges + step-by-step
  outbox-pattern.md           # What the transactional outbox pattern is and why it matters
  message-types.md            # [OutboxMessage], what the generator emits, type discriminator
  dispatchers.md              # IOutboxDispatcher<T>, DefaultOutboxDispatcher, custom impls
  store-adapters.md           # EF Core adapter (schema, migration), InMemory (test usage)
  background-worker.md        # Polling, retry back-off formula, dead-letter, OutboxOptions reference
  dependency-injection.md     # AddOutbox, AddOutboxEfCore, AddOrderPlacedOutbox, lifetime rules
  diagnostics.md              # Summary table of ZO0001–ZO0003 with links
  diagnostics/
    ZO0001.md                 # [OutboxMessage] on interface
    ZO0002.md                 # [OutboxMessage] on static class
    ZO0003.md                 # [OutboxMessage] on nested type
  performance.md              # Zero-alloc design, AOT safety, source-gen vs reflection
  testing.md                  # InMemoryOutboxStore patterns, asserting AllEntries(), worker tests
  cookbook/
    01-ef-core-transaction.md     # Write message in same SaveChangesAsync transaction
    02-custom-dispatcher.md       # Implementing IOutboxDispatcher<T> (HTTP, email, etc.)
    03-mediator-integration.md    # Using ZeroAlloc.Mediator as the dispatcher backend
    04-dead-letter-handling.md    # Inspecting dead-lettered entries, alerting, requeue patterns
    05-testing-with-host.md       # Integration tests using HostBuilder + InMemory store
```

All `.md` files use docusaurus frontmatter (`id`, `title`, `sidebar_position`).

---

## Section 3: README

Structure mirrors `ZeroAlloc.Cache/README.md`:

1. Title + one-paragraph description (source-generated, at-least-once, transactional)
2. Badges: NuGet × 4, Build status, License
3. `dotnet add package` block — all 4 packages with roles annotated
4. **Quick start** — 4 steps: annotate `[OutboxMessage]`, register with DI, write in transaction, implement dispatcher
5. **Features table** — source-generated writers, typed dispatchers, EF Core + InMemory stores, retry + dead-letter, AOT-safe, `IOptions<OutboxOptions>`
6. **Diagnostics table** — ZO0001–ZO0003 with doc links
7. **Documentation** — link to `docs/index.md`
8. License line (MIT)

---

## Excluded

- `LICENSE` file — deferred to a separate cross-repo sweep covering all ZeroAlloc projects
- Mediator integration package (`ZeroAlloc.Outbox.Mediator`) — future package, out of scope here
- Dashboard / dead-letter UI — future package
