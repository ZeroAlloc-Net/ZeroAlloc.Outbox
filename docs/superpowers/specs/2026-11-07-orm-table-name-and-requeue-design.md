# Outbox.Orm: per-pipeline table name, several pipelines per connection, RequeueAsync

Issue: ZeroAlloc-Net/ZeroAlloc.Outbox#434. Status: awaiting approval.

## Intent

An application moving from `Outbox.EfCore` to `Outbox.Orm` must keep its pipeline layout. It
runs one database with an existing quoted, mixed-case table (`"OutboxMessages"`) and a second,
independent ORM pipeline on the unquoted `outboxmessages` table. Three gaps block the move: the
table name is fixed, only one ORM pipeline works per connection, and `OrmOutboxStore` has no
`RequeueAsync`.

## Goals

- A per-pipeline `TableName`. Quoting is part of the contract: the value is used verbatim.
- Two named ORM pipelines on one connection, each claiming, leasing and marking only its own table.
- `RequeueAsync` on the ORM store, reachable for a named pipeline.
- An integration test proving the EF Core to ORM switch on a pre-existing quoted table.

## Non-goals

- The rest of `IOutboxDashboardStore` (snapshot, throughput, cancel, force-dispatch).
- Changing the default: unquoted `OutboxMessages`, so existing databases do not move.
- Validating or quoting the table name for the user (see Risks).

## Constraints

- The statements are compile-time `[Command]` / `[Query]` literals; the ORM has no table-name
  placeholder, so a runtime name cannot be passed to them.
- `OrmOutboxStore` docs deliberately avoid a half-working `IOutboxDashboardStore`.
- The existing public constructors and `WithOrm` overloads stay source- and binary-compatible
  (api-compat covers every package). New parameters go on new overloads, never as optional
  parameters on shipped signatures.

## Approaches

- **A. Runtime SQL builder (chosen).** An internal `OutboxSql` builds each statement from
  `TableName` when the store is created and runs it on `IAsyncDbConnection` with bound parameters.
  Ships in this repo alone. Loses the generated binding for these statements.
- **B. ORM `{table}` placeholder.** Keeps generated code but waits on an ORM release and a floor
  bump. Filed as a follow-up issue on ZeroAlloc.ORM so the builder can be replaced later.
- **C. A generated repository per table, declared at compile time.** Rejected: the table is
  chosen at registration time, per pipeline.

## Design

1. **Options.** A new `OrmOutboxOptions { string TableName = "OutboxMessages" }`. New overloads
   `WithOrm(IOutboxBuilder, OutboxOrmDialect, Action<OrmOutboxOptions>)` and the two named-pipeline
   forms. The existing overloads are unchanged and use the default.
2. **Store.** `OrmOutboxStore` gets a constructor taking the options. The three claim queries and
   the repository statements move to `OutboxSql(dialect, tableName)`. Parameter names, the
   `Status` / `LockedBy` guards and the claim spellings per dialect stay as they are.
3. **Registration guard.** `OrmOutboxStoreFactory` carries the table. `Register` conflicts when the
   same pipeline re-registers with another table, and the shared-connection check compares
   connection and table, so the same table on one connection from two pipelines still throws.
4. **Requeue.** A new public `IOutboxRequeue { ValueTask RequeueAsync(OutboxMessageId, CancellationToken) }`
   in `ZeroAlloc.Outbox`. `OrmOutboxStore` implements it with one conditional `UPDATE` matching the
   EF Core store: from dead-letter to pending, retry count 0, `NextRetryAt` now, error, `ProcessedAt`
   and lease cleared. A miss reads the row to throw "not found" or "cannot be requeued in state X",
   as EF Core does. `WithOrm` registers it keyed per pipeline, and unkeyed for the default one.
5. **Migrations.** `OutboxOrmMigrations.For(dialect, tableName)` creates a table, with index names
   derived from it, for a pipeline that needs its own table. The history source name includes the
   table so each runs once. The fixed name for the default table does not change.
6. **Docs.** Update `docs/store-adapters.md` and the `WithOrm` remarks.

## Tests

- Unit: registration guard (same connection, different tables works; same table throws).
- Integration (SQLite and Postgres via Testcontainers; SQL Server where the Orm tests already run
  it): a quoted-name table holds one pending and one leased row; switching the pipeline to the ORM
  store delivers both once. A dead-lettered row requeued through `IOutboxRequeue` is delivered. The
  second pipeline's table is untouched throughout.
- Allocation gates and AOT smoke still pass with the runtime builder.

## Risks and left to the builder

- `TableName` is spliced into SQL, so it is trusted configuration, not user input. Document that.
  A cheap sanity check (non-empty, no `;`) is the builder's call.
- Quoted names differ by provider (`"x"` vs `[x]`); the value is passed verbatim and the docs show
  each.
- The optional two-stock-workers case is a stretch goal, not required.
- Release: purely additive, so a minor bump.
