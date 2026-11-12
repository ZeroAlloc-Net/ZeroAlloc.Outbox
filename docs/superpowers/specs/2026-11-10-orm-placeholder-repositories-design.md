# Outbox.Orm: generated repositories on ZeroAlloc.ORM placeholders

**Status:** design, awaiting approval · **Date:** 2026-11-10
**Origin:** ZeroAlloc.ORM#464 (shipped in ZeroAlloc.ORM 2.16.0), follow-up to this repo's #434 and PR #437.
**Related:** `2026-11-07-orm-table-name-and-requeue-design.md`, which chose the runtime builder this replaces.

## 1. Intent

Outbox 4.5.0 let an application choose the outbox table. Because `[Command]` and `[Query]` SQL was a compile-time
literal, `OutboxSql` builds the statements at runtime and `OutboxMessageStatements` runs them through
`IAsyncDbConnection` with hand-written parameter binding and row reading. ZeroAlloc.ORM 2.16.0 now accepts `{table}`
and `{col:Name}` in attribute SQL, resolved from members of the repository class. This change puts the statements
back in generated code and deletes `OutboxSql`.

## 2. Goals, non-goals

**Goals**

- Every outbox statement is a `[Command]` or `[Query]` method with `{table}` and `{col:Name}`, so binding and
  materialisation are generated again.
- `OutboxSql`, `OutboxMessageStatements` and `OutboxSqlTests` are deleted.
- Behaviour is unchanged: the same SQL text per dialect, the same table and `QuoteColumns` handling, the same
  allocation budget in `OrmOutboxStoreAllocationTests`.

**Non-goals**

- No public API change. `OrmOutboxOptions.TableName`, `QuoteColumns` and the registration conflict checks stay as
  they are.
- No change to the migrations (`OutboxOrmMigrations`) or the EF Core switchover tests' expectations.
- No new ORM features. A runtime quote style in the ORM is a possible later step, not part of this.

## 3. Decision made with the owner

**Three full repositories, one per dialect.** `{col:Name}` takes its quote style from the `[Dialect]` declared on the
repository class, which is fixed at compile time, while Outbox's dialect is a runtime option, and the batch claim is
spelled differently in all three dialects anyway. So there is one generated class per dialect, each with all ten
statements and its own `[Dialect]`. About 30 partial methods; the nine ANSI statements repeat three times, each as
plain SQL. Registration picks the class from `OutboxOrmDialect`.

Rejected: five classes with a shared ANSI set (declares PostgreSql for SQLite, which changes parameter naming and
DbType checks); a runtime quote style in the ORM first (waits on another ORM release cycle).

## 4. Design

- New internal classes `SqliteOutboxRepository`, `PostgresOutboxRepository` and `SqlServerOutboxRepository`, each
  `partial`, each with `[Dialect(SqlDialect.X)]` and a primary constructor
  `(IAsyncDbConnection connection, string table, bool quoteColumns)`. The ORM resolves `{table}` to `table` and
  `{col:...}` to `quoteColumns` by name.
- Methods mirror `OutboxMessageStatements` one to one: `InsertAsync`, `InsertInTransactionAsync`,
  `MarkProcessedAsync`, `MarkForRetryAsync`, `DeadLetterAsync`, `ReleaseLeaseAsync`, `RenewLeaseAsync`,
  `RequeueAsync`, `ReadStateAsync`, `ClaimPendingAsync`. Parameter names stay as they are, so the SQL text is the one
  `OutboxSql` builds today.
- A small internal interface (`IOutboxStatements`) lists those ten methods so `OrmOutboxStore` holds one reference
  whichever dialect is registered. The exact shape of `ReadStateAsync`'s result (a small record instead of the nullable
  tuple) is the builder's call.
- `OrmOutboxStoreFactory` and `OrmOutboxStore` construct the repository for the registered dialect, table and
  `QuoteColumns` value. The static statement cache goes away: the ORM caches the built SQL per distinct value process-wide.
- `ZeroAlloc.ORM` and `ZeroAlloc.ORM.Generator` package references move to 2.16.1. 2.16.0 was the first version with
  placeholders, but it emitted a per-instance memo field per method that broke the allocation budget; 2.16.1
  keeps it static. This is a floor bump, so it stays a plain `PackageReference` change.
- The connection open and close behaviour of `OutboxMessageStatements` is preserved by the generated code; the
  builder confirms with the existing store tests that a connection that was already open stays open.

## 5. Tests

- The existing store, claim, switchover, named-pipeline and allocation tests stay and must pass unchanged, against
  SQLite always and PostgreSQL and SQL Server where the environment has them.
- `OutboxSqlTests` goes. Its coverage is replaced by tests that read the generated statements' effect: a store
  with `QuoteColumns` against a table created with quoted mixed-case columns, per dialect available.
- The allocation test for the store must stay within its budget; if a generated method allocates more than the
  hand-written one, that is a finding against the ORM, not a reason to raise the budget.

## 6. Constraints

- Branch off `origin/main`. A `refactor:` commit with no public API change does not make a release by itself. Releases
  are batched cycles decided by the owner; no release PR is merged.
- Commit bodies at most 100 characters per line, no nested parentheses, no trailer, no session link.
- Shared VM: build and test one project at a time. Run `dotnet test -c Release` as well as Debug before pushing.

## 7. Left to the builder

- The interface name and the result type of `ReadStateAsync`.
- Whether the dialect-specific `Claim` doc comments move onto the repository classes (they should; the text explains
  why each dialect spells the claim differently).
- Test layout for the quoting round trip.
