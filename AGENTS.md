# AGENTS.md: ZeroAlloc.Outbox

Source-generated transactional outbox for .NET. A message marked `[OutboxMessage]` gets a typed
`IOutboxWriter<T>` and dispatcher from a Roslyn generator, and a polling worker claims, dispatches,
retries and dead-letters rows. It ships as ten NuGet packages: the core (`ZeroAlloc.Outbox`, which
carries the generator as an analyzer), stores (`InMemory`, `EfCore`, `Orm`), bridges (`Mediator`,
`Resilience`, `Telemetry`) and the `Dashboard` with its `Dashboard.Blazor` component.

## Layout

- `src/ZeroAlloc.Outbox.Generator` targets `netstandard2.0` and is not a package of its own: the core
  project references it with `OutputItemType="Analyzer"`. Its output is pinned by Verify snapshots in
  `tests/ZeroAlloc.Outbox.Generator.Tests/Snapshots/*.verified.cs`. Review a `*.received.*` diff
  before you accept it as the new `.verified.` file.
- `src/Shared/OutboxStoreRegistration.cs` is compiled into several store projects.
- Not shipped: `tests/`, `benchmarks/`, `samples/` (`AotSmoke`, `AotSmoke.Adapters`, `DashboardSample`).
- Public API is locked per package by `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. New
  public API goes in `Unshipped`. RS0016 and RS0017 are errors, so a missing entry breaks the build.
  The release PR moves the entries to `Shipped` (the `ship-release-tracking` job in
  `release-please.yml`), and `release-tracking` fails the PR if that move is missing. Do the same for
  generator diagnostics in `AnalyzerReleases.Unshipped.md`. `Dashboard.Blazor` has no API files.
- `apicompat-suppressions.xml` lists the intentional v4.0.0 breaks. The `api-compat` job compares each
  package with its published baseline, so add to it only for a deliberate break.
- `Version` in a package csproj is the placeholder `0.0.0-local`. `dotnet pack` fails (ZAVERSION001,
  ZAVERSION002) unless `-p:Version` was passed to the build as well as to the pack.

## Build and test

Prerequisites: the SDK `global.json` pins, 11.0.100 (`rollForward: latestMinor`). Test projects need the
runtime of each target framework:

| Test project | Targets | Runtimes |
|---|---|---|
| `ZeroAlloc.Outbox.Tests` | `net8.0`, `net10.0`, `net11.0` | .NET 8 and ASP.NET Core 8 (EF Core 9 and `TestHost` 8), 10 and 11 |
| `Generator.Tests`, `Orm.Tests`, `Mediator.Tests`, `Resilience.Tests`, `Telemetry.Tests` | `net10.0`, `net11.0` | .NET 10 and 11 |

`Orm.Tests` and `ZeroAlloc.Outbox.Tests` start PostgreSQL and SQL Server containers through
Testcontainers, so they need a running Docker daemon. Library targets: the core and most packages
build `net8.0` to `net11.0`; `Orm` builds only `net10.0` and `net11.0`.

CI job `build` in `.github/workflows/ci.yml`:

```bash
dotnet tool restore
VERSION=$(dotnet gitversion /showvariable SemVer)
dotnet restore
dotnet build --no-restore -c Release -p:Version=$VERSION
dotnet test --no-build -c Release --verbosity normal --collect "XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
```

Its pack step runs after that build, with the same `$VERSION`:

```bash
mkdir -p ./artifacts
for csproj in src/*/*.csproj; do
  grep -q "<IsPackable>false</IsPackable>" "$csproj" && continue
  dotnet pack "$csproj" --no-build -c Release -p:Version="$VERSION" -o ./artifacts
done
```

The coverage gate (`MarcelRoozekrans/ci-baseline/actions/coverage-gate`, reading
`TestResults/**/coverage.cobertura.xml`) enforces a line-coverage floor on production code. The test
command above produces the reports but does not apply the floor. `codecov.yml` also sets a project
drop check of 1%.

CI job `aot-smoke` (needs `clang` and `zlib1g-dev`; the SDK from `global.json`):

```bash
dotnet restore samples/ZeroAlloc.Outbox.AotSmoke/ZeroAlloc.Outbox.AotSmoke.csproj
dotnet publish samples/ZeroAlloc.Outbox.AotSmoke/ZeroAlloc.Outbox.AotSmoke.csproj -r linux-x64 -c Release -o ./aot-out
./aot-out/ZeroAlloc.Outbox.AotSmoke
dotnet restore samples/ZeroAlloc.Outbox.AotSmoke.Adapters/ZeroAlloc.Outbox.AotSmoke.Adapters.csproj
dotnet publish samples/ZeroAlloc.Outbox.AotSmoke.Adapters/ZeroAlloc.Outbox.AotSmoke.Adapters.csproj -r linux-x64 -c Release -o ./aot-adapters-out
./aot-adapters-out/ZeroAlloc.Outbox.AotSmoke.Adapters
```

Workflow `benchmarks.yml`: job `smoke` runs the shared `benchmark-smoke.yml` on
`benchmarks/ZeroAlloc.Outbox.Benchmarks`; the manual job `full` runs:

```bash
dotnet build benchmarks/ZeroAlloc.Outbox.Benchmarks -c Release
dotnet run --project benchmarks/ZeroAlloc.Outbox.Benchmarks -c Release --no-build -- --filter "${FILTER:-*}"
```

Inner loop, scoped to one project (verified on this repo, not run by a CI job as written):

```bash
dotnet test tests/ZeroAlloc.Outbox.Generator.Tests -c Release -f net10.0
dotnet test tests/ZeroAlloc.Outbox.Tests -c Release -f net8.0 --filter "FullyQualifiedName~OutboxWorkerAllocationTests"
```

CI job `sdk10-consumer` needs a 10.0.1xx SDK; `tests/ZeroAlloc.Outbox.Sdk10Smoke/global.json` pins
10.0.100 with `rollForward: latestPatch`, so a newer band cannot stand in for it. It builds and runs a
consumer of the packed packages with Roslyn 5.0:

```bash
unset v; r=$(od -An -N4 -tx4 /dev/urandom | tr -d " ") && [ -n "$r" ] && v=9.9.9-local.sha$(git rev-parse --short HEAD).$(date +%s).r$r
dotnet pack src/ZeroAlloc.Outbox -c Release -o artifacts -p:Version="${v:?no random suffix}"
dotnet pack src/ZeroAlloc.Outbox.InMemory -c Release -o artifacts -p:Version="${v:?no random suffix}"
cd tests/ZeroAlloc.Outbox.Sdk10Smoke/Consumer
dotnet run -c Release -p:ZeroAllocOutboxVersion="${v:?no random suffix}"
```

Each step stops when the random suffix cannot be generated. Runs in one checkout share its `obj/` and
`bin/` folders, so they must not overlap: give each concurrent run its own worktree. `CS9057` is an
error in the consumer, so a generator from any package in its graph that this SDK skips fails it.

CI job `roslyn-floor` calls the shared `roslyn-floor.yml` from ZeroAlloc-Net/.github: it packs the
solution and fails when a packed DLL references Roslyn above 5.0.0.

## Conventions and gotchas
- The generator references `Microsoft.CodeAnalysis.CSharp` 5.0.0, the compiler of SDK band 10.0.1xx, the
  oldest supported SDK. A compiler older than the Roslyn the generator references skips it with `CS9057`,
  and nothing is generated. The shared Renovate preset holds it below 5.1. If a newer Roslyn API is
  needed, change the code; raise the floor only together with the minimum SDK. Test projects may use a
  newer Roslyn.

- Run allocation tests with `-c Release`. `[OptimizedBuildFact]` skips them on a Debug build, because
  an unoptimised `async` method allocates a state-machine class and the figure would measure the compiler.
- `ZeroAlloc.Outbox.EfCore` is not in the AOT smoke on purpose: EF Core queries run under NativeAOT only
  when precompiled, and this package's own queries never are. `Dashboard.Blazor` is not either, as there
  is no NativeAOT host for Razor components. The reason is written in the Adapters sample csproj. Both are
  exceptions to the org rule that every package works under NativeAOT and has smoke coverage.
- The EfCore project pins EF Core 9 for `net8.0` and `net9.0`, and EF Core 10 for `net10.0` and `net11.0`.
  The `net8.0` test target exists to run the EF Core store against EF Core 9.
- The benchmark project targets `net10.0` only, and the AOT smoke samples `net11.0` only. This is how
  the repo is set up, not a defect to fix.
- Analyzers (Meziantou, Roslynator, ZeroAlloc.Analyzers) and `TreatWarningsAsErrors` apply to every
  project through `Directory.Build.props`. Fix a warning at its source.

<!-- org-core:start sha256=8faf417fa86f11bd8ad9bc98dfa4683fba44e440291e84c6cb9b0ec3ca0c0e7e (generated from docker-nuc scripts/agents-md/org-core-zeroalloc.md; do not edit here) -->
## Org rules

These rules hold in every ZeroAlloc-Net repo. Each library makes two promises: it works under
NativeAOT and trimming, and its hot path allocates nothing. The 2026-10-11 sweep found 16 of 28 repos
allocating on the hot path, most with docs claiming 0 B.

### Hot path

The operation a consumer runs repeatedly: `Send`, a cache hit, `Validate`, `Map`, `Serialize`, a
resilience call, a state transition, an append, a worker poll, including the idle poll. Startup and
registration are not hot path. For a source generator, the hot path is the code it emits: judge the
snapshots and the generated output.

### Allocation traps the sweep found

| Pattern | Instead |
|---|---|
| `async ValueTask` that only awaits and returns | `return new ValueTask<T>(inner.XAsync(ct));` when the method only forwards a `Task<T>` (return a forwarded `ValueTask<T>` directly). The wrapper stores a synchronous throw in the returned task; the direct return lets it, and the cancellation status of the callee, reach the caller. Keep the `async` wrapper when callers depend on either |
| `Task.WhenAll` over tasks that already completed | skip the await only when every task has `IsCompletedSuccessfully` (`Status == TaskStatus.RanToCompletion` on `netstandard2.0`); otherwise `await Task.WhenAll`, which also reports faults of completed tasks |
| `TagList` or log `params object[]` built before `Enabled`/`IsEnabled` | check first, or `LoggerMessage.Define` |
| event args allocated before checking for subscribers | copy the handler to a local, check, then allocate |
| `new Serializer()` / `new Utf8JsonWriter()` per call | cache stateless instances, reuse writers with `Reset` |
| `GetString()` / `ToString()` round-trips in JSON converters | parse and format through spans |
| `ToArray()` / `ToList()` copies between layers | pass `ReadOnlyMemory<T>` or spans, copy once at most |
| LINQ in emitted code | emit `for` loops |
| `yield` iterator behind a public `GetEnumerator` | public struct enumerator, from the first release |
| `Enum.IsDefined(typeof(E), v)` with a boxed value, `decimal.GetBits(v)` to read the scale | `Enum.IsDefined<E>(v)` when `v` has type `E` (.NET 5 or later, so not on .NET Framework, `netstandard2.0`, `netstandard2.1` or .NET Core 3.1); `v.Scale` for a scale read only (.NET 7 or later, never for the coefficient or sign). Keep the old call on older targets |
| configuration parsed or read per call | parse once and cache |
| an `AsyncLocal` write per call | pass state explicitly |
| a capturing lambda or method-group delegate per call | cache the delegate in a field, or a static lambda |
| a per-key object that is never removed | remove on release; otherwise it is a leak |

Inherent allocations are fine when documented: the consumer's own result, an exception on a failure
path, a one-time cache fill.

### Tests and benchmarks

- Every hot path has an allocation gate test (`AllocationGate` from ZeroAlloc.TestHelpers) that measures
  the whole public call, including any state setup.
- Budgets are exact. If a path allocates, assert that number and comment what it is.
- Test the generated code, not a hand-written stand-in, and cover every shape: each attribute, nested
  collections, and the asynchronous case with a fake that returns pending tasks.
- Benchmarks run in CI through the shared `benchmark-smoke.yml` and target the library's LTS
  frameworks. The benchmark body allocates nothing besides the library itself. Run locally with
  `--inProcess`.
- Publish exact allocations and same-run ratios against a `Baseline`, never absolute timings. Run with
  `--launchCount 3`, pin CPUs with `taskset`, give the spread, and label every table.

### NativeAOT and trimming

- Packable runtime projects, and projects consumers publish, set `IsAotCompatible=true` on modern
  target frameworks. Analyzers and source generators target `netstandard2.0`.
- No reflection or runtime code generation on a supported path. An opt-in reflection fallback carries
  honest `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. Every suppression must be true;
  remove dead ones.
- The AOT smoke sample publishes with `TreatWarningsAsErrors=true`. If a third-party warning really
  blocks it, use a narrow, justified suppression for that warning. `WarningsNotAsErrors` selects codes,
  not assemblies, so it would also downgrade the same warning from generated code.
- The smoke runs the hot path of every package in the repo. A package without a smoke has unknown AOT
  status. Adapters run against real services in CI service containers; publish-only is a last resort.
  App and template smokes make authenticated requests.

### Docs and shipping

- Docs match measurements. After a hot-path change, re-measure and update `docs/performance.md` and the
  README. Never write "0 B" without a test that asserts it.
- New public API is `feat:`, even for a performance change. Public API changes are additive: never add
  an optional parameter to a shipped signature. A breaking end state goes in a `next-major` issue.
- A behaviour change gets a `## Behaviour change` section in the PR body.
- Conventional Commits with release-please. Subjects start lowercase, body lines stay within 100
  characters, and a body never contains nested parentheses: release-please drops the whole commit.
- Local packs use `9.9.9-local.<sha>`. After a release, verify the package on NuGet.
- Fix review findings in the PR; file an issue only for what cannot go in it.

Full rules, template wiring lessons and the reasons behind each rule: https://github.com/ZeroAlloc-Net/.github/blob/main/AGENTS.md
<!-- org-core:end -->
