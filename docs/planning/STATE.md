# STATE: Outbox.Orm on ZeroAlloc.ORM placeholders

Updated 2026-11-10. Branch `refactor/outbox-orm-placeholders` (worktree `ZeroAlloc.Outbox-placeholders`).
Design: `docs/superpowers/specs/2026-11-10-orm-placeholder-repositories-design.md` (approved).
Plan: `docs/superpowers/plans/2026-11-10-orm-placeholder-repositories.md`.

## Position

Plan tasks 1 to 4 are done and committed, each reviewed (task review plus fix rounds). Task 5, the final
whole-branch review, verification and the PR, is NOT done. The branch is not merged and has no PR.

Delivered: `SqliteOutboxRepository`, `PostgresOutboxRepository`, `SqlServerOutboxRepository`,
`IOutboxStatements`, `OutboxRowState`, `OutboxStatements.Create`, `OrmOutboxStore` and the registration
factory moved onto them, `OutboxSql`, `OutboxMessageStatements` and the parity tests deleted, ORM package
floor 2.16.0, four new store tests. Public API files unchanged. All functional tests pass in Debug and
Release against SQLite, PostgreSQL and SQL Server containers.

## Blocker: 4 red allocation tests, root cause is in ZeroAlloc.ORM

`OrmOutboxStoreAllocationTests` fails (4 tests, Debug and Release). The owner chose to fix it in the ORM
first, not to raise the budget.

- Constructing a store went from 72 B to 168 B; the scope test reads 352 B against a limit under 300 B.
- About 88 B is ZeroAlloc.ORM.Generator 2.16.0: for every `[Command]` or `[Query]` method that uses
  placeholders the generator emits a per-INSTANCE memo field (`__sql{i}_{Method}`, one reference each) in
  front of a static `ConcurrentDictionary` cache. A repository with 10 such methods carries 10 fields, 80 B
  of extra instance size, and Outbox builds a repository per DI scope, so every scope pays it and rarely
  reuses the memo. The old hand-written runner was 32 B.
- The other 8 B are the dialect, table and quoteColumns fields the store keeps so
  `EnqueueInTransactionAsync` can build statements over `transaction.Connection`. That one is Outbox-side
  and optional (it would need a method on `IOutboxStatements`); decide after the ORM fix.

## Next steps

1. ZeroAlloc.ORM (repo `/home/marcel/Projects/Prive/ZeroAlloc/ZeroAlloc.ORM`, GitHub ZeroAlloc-Net/ZeroAlloc.ORM):
   brainstorm and fix the generator so a placeholder method keeps NO per-instance state. Direction chosen by
   the owner: a static per-method memo (a static field holding the last resolved holder plus the key it was
   built for; check by reference equality on the member values, fall back to the existing dictionary) so a
   repository instance costs nothing extra and a call still allocates nothing after warm-up. Design points to
   settle: two repositories alternating different tables must not allocate (they use the dictionary); keep the
   null or blank value `ArgumentException`; keep the benign-race publication rule (one immutable holder,
   Volatile). Relevant code: `EmitPlaceholderSqlCache`, `EmitPlaceholderSqlHolder` and
   `BuildCommandTextAssignment` in `src/ZeroAlloc.ORM.Generator/OrmGenerator.cs`; emit tests in
   `tests/ZeroAlloc.ORM.Generator.Tests/Emit/PlaceholderEmitTests.cs`; allocation gate in
   `tests/ZeroAlloc.ORM.Integration.Tests/AllocationGates/TablePlaceholderAllocationTests.cs` (add a gate
   that a repository INSTANCE has no extra size: measure a fresh instance against a literal-SQL one).
   Design doc first, owner approves, then plan, then subagent-driven execution, as for #464.
   ORM rules: Conventional Commits, `fix:` or `perf:` (remember `perf:` maps to a patch bump), no public API
   change, never an optional parameter on a shipped signature, build and test one project at a time, run
   Release as well as Debug, 8 Integration allocation gates already fail in Debug on main, do not merge any
   release PR. Releases are batched cycles decided by the owner.
2. When ZeroAlloc.ORM with the fix is on nuget.org (verify the version list), bump both ZeroAlloc.ORM and
   ZeroAlloc.ORM.Generator references in `src/ZeroAlloc.Outbox.Orm/ZeroAlloc.Outbox.Orm.csproj` to it and
   re-run `OrmOutboxStoreAllocationTests`; the budget stays as it is. If 8 B remain over, ask the owner
   before touching `IOutboxStatements`.
3. Finish plan Task 5: whole-branch review on the most capable model, rebase on `origin/main`, push (a force
   push is denied by the permission classifier; push under a new branch name if the history is rewritten),
   open the PR (`refactor:` title, no public API change, ORM floor bump, what was deleted), watch CI, do not
   merge, never a release PR.

## Rulings so far (Outbox work)

- The state record is `OutboxRowState`, because `OutboxMessageState` is the public status enum in
  `ZeroAlloc.Outbox`. Costs nothing.
- Three full repositories, one per dialect (owner's choice), because `{col:}` quoting comes from the
  compile-time `[Dialect]`.
- Generated code never sets `command.Transaction` for methods without a `tx` parameter, as before; not a
  regression. A SQL Server lock-holder test therefore uses a raw `SqlConnection`.

## Working notes

- Scratch for the SDD run lives in git-ignored `.superpowers/` (ledger and reports). The task reports there
  have the details; if it is gone, `git log` is the record.
- Commit rules: bodies at most 100 characters per line, no nested parentheses, no Claude trailer, no session
  link. Never merge a release PR.
