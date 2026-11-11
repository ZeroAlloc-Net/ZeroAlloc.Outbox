# STATE: Outbox.Orm on ZeroAlloc.ORM placeholders

Updated 2026-11-11. Branch `refactor/outbox-orm-placeholders` (worktree `ZeroAlloc.Outbox-placeholders`).
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

## Position update 2026-11-11

The ORM blocker is resolved. ZeroAlloc.ORM 2.16.1 (PR 471, static per-method memo, verified on nuget.org
including the published generator bytes) removed the per-instance memo fields. The remaining 16 B were
Outbox-side: the store kept table, dialect and quote flag only to rebuild a repository over a caller's
connection. `IOutboxStatements.OnConnection` (internal) does that now, so the store holds a connection and
a repository, and `OrmOutboxStoreAllocationTests` is green at the unchanged 72 B budget. The owner chose
this option. Orm tests: 195 passed in Release and Debug. Branch rebased on origin/main (Renovate had
already moved the ORM floor to 2.16.1).

## Next steps

1. Plan Task 5: whole-branch review on the most capable model, push, open the PR (`refactor:` title, no
   public API change, ORM floor 2.16.1, what was deleted, the OnConnection addition), watch CI, do not
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
