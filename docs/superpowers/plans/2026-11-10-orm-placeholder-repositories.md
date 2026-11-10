# Outbox.Orm on ZeroAlloc.ORM placeholders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the runtime `OutboxSql` and `OutboxMessageStatements` with one generated ZeroAlloc.ORM repository per dialect that uses `{table}` and `{col:Name}`.

**Architecture:** Three internal partial repositories (`Sqlite`, `Postgres`, `SqlServer`), each with its own `[Dialect]` and the ten statements as `[Command]` and `[Query]` methods, behind one internal interface. A small factory builds the right repository for a dialect, connection, table and `QuoteColumns` value, and `OrmOutboxStore` uses it instead of the hand-written statements.

**Tech Stack:** C# net10.0, ZeroAlloc.ORM and ZeroAlloc.ORM.Generator 2.16.0, xUnit v3, SQLite, PostgreSQL and SQL Server containers in the tests.

**Spec:** `docs/superpowers/specs/2026-11-10-orm-placeholder-repositories-design.md`. Placeholder rules: ZeroAlloc.ORM docs (README section "Table and column placeholders"; diagnostics ZAO115 to ZAO118).

## Global Constraints

- No public API change: `PublicAPI.*.txt` stays as it is; `OrmOutboxOptions`, `OrmOutboxStore`'s public constructor and the registration conflict checks keep their behaviour. The new types are `internal`.
- `ZeroAlloc.ORM` and `ZeroAlloc.ORM.Generator` package references are `2.16.0` (both in `src/ZeroAlloc.Outbox.Orm/ZeroAlloc.Outbox.Orm.csproj`).
- The SQL text per dialect is exactly what `OutboxSql` builds today, with `{table}` for the table and `{col:Name}` for each column. Parameter names and order per method equal the corresponding `OutboxMessageStatements` method.
- Each repository's primary constructor is `(IAsyncDbConnection connection, string table, bool quoteColumns)` with `[Dialect(SqlDialect.X)]`. Do not add a property named `Table` or `QuoteColumns` (it would be a second candidate for the placeholder and fail with ZAO115).
- The table value is trusted configuration: spliced verbatim, never validated by the generator.
- Conventional Commits, `refactor:` for these changes, no public API change. Commit body lines at most 100 characters, no nested parentheses, no Co-Authored-By or Claude trailer, no session link.
- Shared VM: build and test ONE project at a time. Run `dotnet test -c Release` as well as Debug before pushing. Do not merge any PR, and never a release PR.
- Work in the worktree `ZeroAlloc.Outbox-placeholders` on `refactor/outbox-orm-placeholders`.

## Review Focus

- Quoted mixed-case columns round trip against a real table on each dialect, as EF Core creates it on PostgreSQL (`"Id"`). Pinned in Tasks 1 to 3.
- `EnqueueInTransactionAsync` writes through the transaction's connection, not the store's own, and a connection that was already open stays open. Task 4.
- A `DateTimeOffset` written by the generated insert reads back equal, and rows written by the EF Core switchover tests still claim and mark correctly. Task 4.
- The claim returns exactly the rows it leased on each dialect, under concurrent claimers where the existing claim tests already cover it. Tasks 1 to 3 and 4.
- The store's allocation budget is not raised. Task 4.

---

## File Structure

- Create `src/ZeroAlloc.Outbox.Orm/IOutboxStatements.cs`: the ten-method internal interface and the `OutboxMessageState` record.
- Create `src/ZeroAlloc.Outbox.Orm/SqliteOutboxRepository.cs`, `PostgresOutboxRepository.cs`, `SqlServerOutboxRepository.cs`.
- Create `src/ZeroAlloc.Outbox.Orm/OutboxStatements.cs`: the factory.
- Modify `OrmOutboxStore.cs`, `OrmOutboxServiceCollectionExtensions.cs`, `ZeroAlloc.Outbox.Orm.csproj`.
- Delete `OutboxSql.cs`, `OutboxMessageStatements.cs`, `tests/ZeroAlloc.Outbox.Orm.Tests/OutboxSqlTests.cs`.
- New tests: `tests/ZeroAlloc.Outbox.Orm.Tests/SqliteOutboxRepositoryTests.cs`, `PostgresOutboxRepositoryTests.cs`, `SqlServerOutboxRepositoryTests.cs`, in the style of the existing `*OutboxClaimTests.cs` and `SqliteFixture.cs`.

### Task 1: Interface and the SQLite repository

**Files:**
- Create: `IOutboxStatements.cs`, `SqliteOutboxRepository.cs`
- Modify: `ZeroAlloc.Outbox.Orm.csproj` (both ORM references to 2.16.0)
- Test: `SqliteOutboxRepositoryTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record OutboxMessageState(int Status, int RetryCount)`
  - `internal interface IOutboxStatements` with `InsertAsync`, `InsertInTransactionAsync`, `MarkProcessedAsync`, `MarkForRetryAsync`, `DeadLetterAsync`, `ReleaseLeaseAsync`, `RenewLeaseAsync`, `RequeueAsync` (each `Task<int>`), `ReadStateAsync` (`Task<OutboxMessageState?>`) and `ClaimPendingAsync` (`Task<IReadOnlyList<OutboxMessageRow>>`). Parameter names, types and order equal the same-named `OutboxMessageStatements` methods, except `ReadStateAsync`'s result.
  - `internal sealed partial class SqliteOutboxRepository(IAsyncDbConnection connection, string table, bool quoteColumns) : IOutboxStatements` with `[Dialect(SqlDialect.Sqlite)]`.

- [ ] **Step 1: Failing tests** in `SqliteOutboxRepositoryTests`, over a real SQLite table created like the existing fixtures create it: `Insert_ThenReadState_RoundTrips`, `InsertInTransaction_EnlistsInTheCallersTransaction` (rolled back, row absent), `MarkProcessed_RequiresPendingStatusAndLease` (returns 0 for a wrong host, 1 for the lease holder), `MarkForRetry_`, `DeadLetter_`, `ReleaseLease_`, `RenewLease_`, `Requeue_ResetsADeadLetter` and `Requeue_IgnoresANonDeadLetter` (each asserting the row count returned and the row state), `ClaimPending_LeasesAndReturnsExactlyThoseRows` (batch size 2 of 3 due rows), `CustomTable_UsesTheConfiguredName` (table `"Custom Outbox"` quoted by the caller), and `QuotedColumns_RoundTrip` against a table created with quoted mixed-case column names, then the same statements with `quoteColumns: false` against an unquoted table. Take the SQL semantics from the matching tests in `OrmOutboxStoreTests` and `OrmOutboxClaimTests`.
- [ ] **Step 2: Run to verify failure.** `dotnet test tests/ZeroAlloc.Outbox.Orm.Tests -c Debug --filter SqliteOutboxRepositoryTests`. Expected: compile error, type missing.
- [ ] **Step 3: Implement** `IOutboxStatements`, `OutboxMessageState` and `SqliteOutboxRepository`. Each method is a `[Command]` or `[Query]` whose SQL is the statement `OutboxSql` builds, with `{table}` and `{col:X}` in place of the table and `Q("X")`. For SQLite the claim is the `UPDATE … RETURNING` over a `LIMIT` subquery. Move the explanatory doc comments for each statement and for the claim from `OutboxSql`. Bump both ORM package references to 2.16.0.
- [ ] **Step 4: Run to verify pass,** then the whole `ZeroAlloc.Outbox.Orm.Tests` project in Debug. Expected: all pass except tests that need containers you cannot reach.
- [ ] **Step 5: Commit.** `refactor: add the SQLite outbox repository on ORM placeholders`

### Task 2: The PostgreSQL repository

**Files:**
- Create: `PostgresOutboxRepository.cs`
- Test: `PostgresOutboxRepositoryTests.cs`

**Interfaces:**
- Consumes: `IOutboxStatements` from Task 1.
- Produces: `internal sealed partial class PostgresOutboxRepository(IAsyncDbConnection connection, string table, bool quoteColumns) : IOutboxStatements` with `[Dialect(SqlDialect.PostgreSql)]`.

- [ ] **Step 1: Failing tests,** the Task 1 list over a PostgreSQL container as `PostgresOutboxClaimTests` does, including `QuotedColumns_RoundTrip` against a table created with quoted mixed-case columns such as EF Core makes, and `ClaimPending_SkipsRowsLockedByAnotherClaimer` (two concurrent claimers get disjoint rows).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** with the claim as the `MATERIALIZED` CTE with `FOR UPDATE SKIP LOCKED` then `UPDATE {table} t … FROM c … RETURNING t.{col:Id}, …`. Move the CTE rationale doc comment from `OutboxSql`.
- [ ] **Step 4: Run to verify pass** for the new tests, then the whole project in Debug.
- [ ] **Step 5: Commit.** `refactor: add the PostgreSQL outbox repository on ORM placeholders`

### Task 3: The SQL Server repository

**Files:**
- Create: `SqlServerOutboxRepository.cs`
- Test: `SqlServerOutboxRepositoryTests.cs`

**Interfaces:**
- Consumes: `IOutboxStatements` from Task 1.
- Produces: `internal sealed partial class SqlServerOutboxRepository(IAsyncDbConnection connection, string table, bool quoteColumns) : IOutboxStatements` with `[Dialect(SqlDialect.SqlServer)]`.

- [ ] **Step 1: Failing tests,** the Task 1 list over a SQL Server container as `SqlServerOutboxClaimTests` does, with `QuotedColumns_RoundTrip` using `[Id]` style columns.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** with the claim as the updatable `TOP (@batchSize)` CTE with `ROWLOCK, READPAST, UPDLOCK` hints and an `OUTPUT inserted.{col:Id}, …` clause. Move the rationale doc comment.
- [ ] **Step 4: Run to verify pass,** then the whole project in Debug.
- [ ] **Step 5: Commit.** `refactor: add the SQL Server outbox repository on ORM placeholders`

### Task 4: Wire the store and delete the runtime builder

**Files:**
- Create: `OutboxStatements.cs`
- Modify: `OrmOutboxStore.cs`, `OrmOutboxServiceCollectionExtensions.cs`
- Delete: `OutboxSql.cs`, `OutboxMessageStatements.cs`, `tests/ZeroAlloc.Outbox.Orm.Tests/OutboxSqlTests.cs`

**Interfaces:**
- Consumes: the three repositories from Tasks 1 to 3.
- Produces: `internal static class OutboxStatements` with `internal static IOutboxStatements Create(OutboxOrmDialect dialect, IAsyncDbConnection connection, string table, bool quoteColumns)`, throwing `ArgumentOutOfRangeException` for an unknown dialect.

- [ ] **Step 1: Failing test** `OutboxStatementsTests.Create_PicksTheRepositoryForTheDialect` (one row per `OutboxOrmDialect` value, asserting the returned type) and `Create_UnknownDialect_Throws`. Run: expected failure, type missing.
- [ ] **Step 2: Implement `OutboxStatements.Create`** with a `switch` on the dialect.
- [ ] **Step 3: Switch `OrmOutboxStore`** to hold `IOutboxStatements` built by `Create` in its public constructor, and keep `dialect`, `tableName` and `quoteColumns` as fields so `EnqueueInTransactionAsync` builds a statements object over `transaction.Connection` as `new OutboxMessageStatements(transaction.Connection, _sql)` does at line ~201 today. Adapt `ReadStateAsync` callers to `OutboxMessageState?`. Replace the internal `OrmOutboxStore(IAsyncDbConnection, OutboxSql)` constructor with one taking the dialect, table and flag, and update `OrmOutboxStoreFactory` in `OrmOutboxServiceCollectionExtensions.cs` (it keeps `Dialect`, `Connection`, `TableName` and `QuoteColumns` for the registration conflict check; drop only its `_sql` field).
- [ ] **Step 4: Delete** `OutboxSql.cs`, `OutboxMessageStatements.cs` and `OutboxSqlTests.cs`. Search the repo for leftover references (docs, comments, the plan and spec under `docs/superpowers` may keep historical mentions; do not edit those).
- [ ] **Step 5: Run, one project at a time:** the new tests, then `ZeroAlloc.Outbox.Orm.Tests` whole in Debug and in Release, including the allocation tests (budget unchanged) and the EF Core switchover tests. Then build the whole solution in Release with warnings as errors. Expected: everything passes except tests that need unreachable containers, which are named in the report.
- [ ] **Step 6: Commit.** `refactor: run the Orm outbox store on generated repositories`

### Task 5: Verification and PR

- [ ] **Step 1:** Run, one at a time: solution build in Release with warnings as errors; `dotnet test -c Release` for every test project CI runs (read `.github/workflows` for the list and any api-compat or public API check); Debug for `ZeroAlloc.Outbox.Orm.Tests`. Expected: green. Compare any failure with `origin/main` before blaming the change.
- [ ] **Step 2:** `git diff origin/main -- '*PublicAPI*.txt'` is empty.
- [ ] **Step 3:** Rebase on `origin/main` if it moved, push, open a PR titled `refactor: run the Orm outbox store on generated ZeroAlloc.ORM repositories`. The body states that behaviour and public API are unchanged, the ORM floor moves to 2.16.0, and what was deleted. Do not merge.
