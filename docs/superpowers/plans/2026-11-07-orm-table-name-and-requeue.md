# Outbox.Orm table name, shared connection and RequeueAsync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an `Outbox.Orm` pipeline name its own table, let several ORM pipelines share one connection, and give the ORM store `RequeueAsync` (issue #434).

**Architecture:** Replace the compile-time `[Command]`/`[Query]` repository and the three claim queries with one internal runtime SQL builder, `OutboxSql`, keyed on dialect and table name. The table name rides on the registration factory, so the shared-connection guard compares connection and table. Requeue is a new narrow public interface, `IOutboxRequeue`, implemented by `OrmOutboxStore`.

**Tech Stack:** C# / .NET, ZeroAlloc.ORM (`IAsyncDbConnection`), xunit + AwesomeAssertions, Testcontainers (Postgres, SQL Server), SQLite.

**Spec:** `docs/superpowers/specs/2026-11-07-orm-table-name-and-requeue-design.md`

## Global Constraints

- The default table stays `OutboxMessages`, unquoted. Existing databases and every existing test must pass unchanged.
- `TableName` is used verbatim, so the caller supplies any quoting. It is trusted configuration, not user input.
- api-compat covers every package: never add an optional parameter to a shipped signature. New behaviour goes on new overloads and constructors.
- Purely additive, so the release is a minor bump. Commit type `feat:`.
- Commit bodies: lines of at most 100 characters, no nested parentheses, no Claude signature or session link.
- Run builds and tests one project at a time. Do not run parallel Node or .NET builds on the shared VM.

## Review Focus

- A schema-qualified name such as `public."OutboxMessages"` or `[dbo].[OutboxMessages]` must work in every statement, including the SQL Server `WITH (ROWLOCK, READPAST, UPDLOCK)` claim.
- `TableName` null, empty or whitespace throws `ArgumentException` at registration, not at the first poll.
- `RequeueAsync` on an unknown id throws "not found"; on a pending, leased or dispatched row it throws "cannot be requeued in state X" and changes nothing.
- A row requeued while another host still holds a stale lease leaves no lease behind.
- Two pipelines on one connection whose tables differ only in quoting or case are still treated as different tables and both register.

---

### Task 1: Runtime SQL builder replaces the generated repository

**Files:**
- Create: `src/ZeroAlloc.Outbox.Orm/OutboxSql.cs`
- Modify: `src/ZeroAlloc.Outbox.Orm/OrmOutboxStore.cs`
- Delete: `src/ZeroAlloc.Outbox.Orm/OutboxMessageRepository.cs`, `ClaimPostgresPendingOutboxQuery.cs`, `ClaimSqlServerPendingOutboxQuery.cs`, `ClaimSqlitePendingOutboxQuery.cs`, `IPendingOutboxQuery.cs`
- Test: `tests/ZeroAlloc.Outbox.Orm.Tests/OutboxSqlTests.cs`

**Interfaces:**
- Produces: `internal sealed class OutboxSql(OutboxOrmDialect dialect, string tableName)` with one string property per statement: `Insert`, `MarkProcessed`, `MarkForRetry`, `DeadLetter`, `ReleaseLease`, `RenewLease`, `Claim`, `Requeue`. Parameter names are exactly those of the current attributes (`@id`, `@status`, `@pending`, `@hostId`, `@until`, `@now`, `@batchSize`, and so on).
- Produces: `internal sealed class OutboxMessageStatements(IAsyncDbConnection connection, OutboxSql sql)` with the same method names and arguments as today's `OutboxMessageRepository`, plus `ClaimPendingAsync` and `RequeueAsync`, so `OrmOutboxStore` changes little.

- [ ] **Step 1: Write the failing test** `OutboxSqlTests.Statements_name_the_configured_table_in_every_dialect`: for each dialect and for table names `OutboxMessages`, `"OutboxMessages"` and `public."Outbox Messages"`, every statement contains the table name verbatim and no statement still contains the bare literal `OutboxMessages` when a different name is configured.
- [ ] **Step 2: Run it.** `dotnet test tests/ZeroAlloc.Outbox.Orm.Tests --filter OutboxSqlTests`. Expected: FAIL, `OutboxSql` not defined.
- [ ] **Step 3: Implement `OutboxSql`.** Copy the SQL text from the current attributes, with `{tableName}` substituted. Keep the three claim spellings per dialect. The Postgres claim aliases `t` and `c` stay. Index and column names are untouched.
- [ ] **Step 4: Implement `OutboxMessageStatements`.** Run each statement through `IAsyncDbConnection` (create command, bind parameters, `ExecuteNonQueryAsync`; the claim reads rows into `OutboxMessageRow`). Look at how `ZeroAlloc.ORM` and `AdoNet.Async` expose `CreateCommand` and readers, and at the existing generated output under `obj/` for the exact binding, including the `DateTimeOffset` handling per provider. Enlisting in a transaction sets the command's transaction, as `InsertInTransactionAsync` does today.
- [ ] **Step 5: Switch `OrmOutboxStore` to `OutboxMessageStatements`** and add a constructor `OrmOutboxStore(IAsyncDbConnection connection, OutboxOrmDialect dialect, string tableName)`. The two existing constructors call it with `"OutboxMessages"`. Delete the five replaced files.
- [ ] **Step 6: Run the whole Orm test project.** `dotnet test tests/ZeroAlloc.Outbox.Orm.Tests`. Expected: all existing tests pass with the default table, including `OrmOutboxStoreAllocationTests`, the claim tests and the Postgres and SQL Server containers.
- [ ] **Step 7: Check AOT and allocation.** Run the repo's AOT smoke and the allocation gates. If a gate figure rises because of the runtime builder, build the command text once per store, not per call, and re-run before touching a gate.
- [ ] **Step 8: Commit** `refactor: build the Orm store SQL at runtime from a table name`.

### Task 2: Options, overloads and the registration guard

**Files:**
- Create: `src/ZeroAlloc.Outbox.Orm/OrmOutboxOptions.cs`
- Modify: `src/ZeroAlloc.Outbox.Orm/OrmOutboxServiceCollectionExtensions.cs`
- Test: `tests/ZeroAlloc.Outbox.Orm.Tests/OrmOutboxRegistrationTests.cs`, `OrmNamedPipelineTests.cs`

**Interfaces:**
- Consumes: `OrmOutboxStore(IAsyncDbConnection, OutboxOrmDialect, string tableName)` from Task 1.
- Produces: `public sealed class OrmOutboxOptions { public string TableName { get; set; } = "OutboxMessages"; }`
- Produces: new overloads `WithOrm(this IOutboxBuilder, OutboxOrmDialect, Action<OrmOutboxOptions>)`, `WithOrm(this INamedOutboxBuilder, OutboxOrmDialect, object connectionKey, Action<OrmOutboxOptions>)` and `WithOrm(this INamedOutboxBuilder, OutboxOrmDialect, Func<IServiceProvider, IAsyncDbConnection>, Action<OrmOutboxOptions>)`.

- [ ] **Step 1: Write failing tests** in `OrmNamedPipelineTests`:
  - `Two_pipelines_on_the_container_connection_with_different_tables_register` (no throw).
  - `Two_pipelines_on_the_container_connection_with_the_same_table_throw` (`InvalidOperationException`).
  - `Reregistering_one_pipeline_with_another_table_throws`.
  - `Reregistering_with_the_same_table_is_a_noop`.
  - `Blank_table_name_throws_ArgumentException_at_registration` (null, `""`, `"  "`).
  - `Tables_differing_only_in_quoting_are_different_tables`.
- [ ] **Step 2: Run them.** Expected: FAIL, overloads missing.
- [ ] **Step 3: Implement.** `OrmOutboxStoreFactory` gains `TableName`. `Register` takes the table name; the conflict checks add a third comparison (ordinal string equality) next to dialect and connection, and the `ThrowIfSharedWithAnotherPipeline` predicate treats pipelines as shared only when connection and table are both equal. The shared-connection message names the table. Existing overloads pass the default name.
- [ ] **Step 4: Run the Orm test project.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: configure the Orm outbox table name per pipeline`.

### Task 3: RequeueAsync

**Files:**
- Create: `src/ZeroAlloc.Outbox/IOutboxRequeue.cs`
- Modify: `src/ZeroAlloc.Outbox.Orm/OrmOutboxStore.cs`, `OutboxMessageStatements.cs`, `OutboxSql.cs`, `OrmOutboxServiceCollectionExtensions.cs`
- Test: `tests/ZeroAlloc.Outbox.Orm.Tests/OrmOutboxStoreTests.cs`, `OrmNamedPipelineTests.cs`

**Interfaces:**
- Produces: `public interface IOutboxRequeue { ValueTask RequeueAsync(OutboxMessageId id, CancellationToken ct); }`
- Produces: `OrmOutboxStore : IOutboxStore, IOutboxRequeue`. The default pipeline resolves `IOutboxRequeue` unkeyed; a named pipeline resolves it keyed by its name.

- [ ] **Step 1: Write failing tests** in `OrmOutboxStoreTests` (SQLite):
  - `Requeue_moves_a_dead_lettered_row_back_to_pending_due_now`: afterwards `RetryCount` 0, `DeadLetterError` and `ProcessedAt` null, no lease, and `ClaimPendingAsync` returns it.
  - `Requeue_of_an_unknown_id_throws_not_found`.
  - `Requeue_of_a_pending_leased_or_succeeded_row_throws_and_changes_nothing` (theory over the three states).
  - In `OrmNamedPipelineTests`: `Requeue_resolves_per_named_pipeline_and_hits_only_its_table`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** `OutboxSql.Requeue` as one `UPDATE` guarded by `Id = @id AND Status = @deadLetter`, setting status pending, `RetryCount = 0`, `NextRetryAt = @now`, `DeadLetterError`, `ProcessedAt`, `LockedBy` and `LockedUntil` to NULL. `RequeueAsync` on the store: if no row changed, read the row's status and throw `InvalidOperationException("Message {id} not found.")` or `"Message {id} cannot be requeued in state {state}."`, matching `EfCoreOutboxStore.RequeueAsync`. `Register` also registers `IOutboxRequeue` (unkeyed, or keyed by pipeline) from the same store instance.
- [ ] **Step 4: Run the Orm test project.** Expected: PASS.
- [ ] **Step 5: Run api-compat** as CI does (see `apicompat-suppressions.xml` and the workflow). Expected: no breaking change.
- [ ] **Step 6: Commit** `feat: requeue dead-lettered messages in the Orm outbox store`.

### Task 4: Migrations for a second table

**Files:**
- Modify: `src/ZeroAlloc.Outbox.Orm/OutboxOrmMigrations.cs`
- Test: `tests/ZeroAlloc.Outbox.Orm.Tests/OutboxMigrationSourceTests.cs`, `OutboxMigrationTests.cs`

**Interfaces:**
- Produces: `public static IMigrationSource For(OutboxOrmDialect dialect, string tableName)`. `tableName` is a plain identifier (letters, digits, underscore, not starting with a digit); anything else throws `ArgumentException`. The DDL uses it unquoted, with index `IX_{tableName}_Status_NextRetryAt`. The source `Name` is `ZeroAlloc.Outbox.Orm` for `OutboxMessages` and `ZeroAlloc.Outbox.Orm:{tableName}` otherwise, so each table's history is separate and the default name never changes.

- [ ] **Step 1: Write failing tests:** `For_default_table_has_the_fixed_source_name`, `For_another_table_has_its_own_source_name`, `For_rejects_a_quoted_or_qualified_name`, and a SQLite `Two_tables_migrate_side_by_side_on_one_connection` that runs both sources and enqueues into each.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** by templating the existing three dialect scripts on the table name. Keep `Sqlite`, `Postgres` and `SqlServer` returning the existing sources.
- [ ] **Step 4: Run the Orm test project.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: migrate an additional Orm outbox table`.

### Task 5: Switch-over integration test, docs and the ORM follow-up

**Files:**
- Create: `tests/ZeroAlloc.Outbox.Orm.Tests/OrmEfCoreSwitchoverTests.cs`
- Modify: `docs/store-adapters.md`, the `WithOrm` remarks, `README.md` if it lists store capabilities

- [ ] **Step 1: Write the integration test** (Postgres container, EfCore project reference as the Orm tests already have it): create a quoted `"OutboxMessages"` table through the EF Core store; leave one pending row and one row leased by another host with a lease that has expired; create the second pipeline's unquoted `outboxmessages` table with `OutboxOrmMigrations.For`; switch pipeline one to `WithOrm(dialect, o => o.TableName = "\"OutboxMessages\"")`; run the worker; assert both rows are delivered once; dead-letter a third row, call `IOutboxRequeue.RequeueAsync`, assert it is delivered; assert every row of the second table is byte-for-byte unchanged.
- [ ] **Step 2: Run it.** `dotnet test tests/ZeroAlloc.Outbox.Orm.Tests --filter OrmEfCoreSwitchoverTests`. Expected: PASS. Repeat for SQL Server if its container runs in this project already.
- [ ] **Step 3: Docs.** In `docs/store-adapters.md` document `TableName` with per-provider quoting examples, the shared-connection rule (connection and table), `IOutboxRequeue`, `OutboxOrmMigrations.For`, and that the table name is trusted configuration. Remove the claim that only one ORM pipeline works per connection.
- [ ] **Step 4: Full verification.** Build the solution, run the Orm and core test projects one after another, then the AOT smoke. Expected: all green.
- [ ] **Step 5: Commit** `docs: describe the Orm table name and requeue`.
- [ ] **Step 6: File the follow-up.** Open an issue on ZeroAlloc.ORM asking for a table-name placeholder in `[Command]` and `[Query]`, so the runtime builder in `OutboxSql` can return to generated code. Group it into one issue, and check for an existing one first.
- [ ] **Step 7: Open the PR** only when the user asks; the branch is `feat/434-orm-table-name`. The PR closes #434.
