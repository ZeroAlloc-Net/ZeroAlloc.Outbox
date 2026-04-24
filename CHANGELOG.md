# Changelog

## [1.2.1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/compare/v1.2.0...v1.2.1) (2026-04-24)


### Bug Fixes

* use collection expressions, drop redundant using System ([c6fe117](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/c6fe117933ba99dabd7903019d79d9536e5d116d))
* use collection expressions, drop redundant using System ([cbd018b](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/cbd018b359a01eb135c4124aec8ac0cc76f85b18))

## [1.2.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/compare/v1.1.2...v1.2.0) (2026-04-24)


### Features

* **outbox:** use ConcurrentHeapSpanDictionary for in-memory store ([#23](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/23)) ([98a24bc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/98a24bc1cd6a5cc05e51c36c610becceb291f05e))
* telemetry spans, typed OutboxMessageId, Mediator/Resilience wiring + NuGet isolation ([d6412d6](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/d6412d6f9218692e354aeae9222bb4b2ef799b40))
* telemetry, typed OutboxMessageId, Mediator/Resilience + NuGet isolation ([c5a2749](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/c5a274922b0226859caf3422e05fc7041f74f7aa))


### Bug Fixes

* update FakeStore/FakeOutboxStore to OutboxMessageId interface ([52c51cd](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/52c51cdcb1d64e7ddf347a172b182891a3667c2f))

## [1.1.2](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/compare/v1.1.1...v1.1.2) (2026-04-24)


### Bug Fixes

* **dashboard:** render single-bucket throughput as counts, not tiny dots ([#14](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/14)) ([3bd2d72](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/3bd2d72d93415e412677f0986f63e2f246c16d07))

## [1.1.1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/compare/v1.1.0...v1.1.1) (2026-04-23)


### Bug Fixes

* **generator:** emit [UnconditionalSuppressMessage] on WriteAsync/DispatchAsync (closes [#8](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/8)) ([#9](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/9)) ([8548f12](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/8548f120ce1f06968ed8372b4ff8c4c68166bde8))


### Performance Improvements

* add BenchmarkDotNet project measuring OrderPlacedOutboxWriter overhead ([#11](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/11)) ([2ee4c0b](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/2ee4c0bdc8fdb342d0aa691a6daed15fece06b83))

## [1.1.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/compare/v1.0.0...v1.1.0) (2026-04-22)


### Features

* ZeroAlloc.Outbox.Dashboard — operations dashboard with SSE + Blazor component ([#3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/3)) ([dd6f116](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/dd6f116744d97804829e54fa77782b11c2da1afe))


### Bug Fixes

* **packaging:** replace 1x1 placeholder with real Z logo icon ([#5](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/5)) ([1f602d7](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/1f602d7983d933c3e6541afa5eb9dc74ed942e42))

## 1.0.0 (2026-04-20)


### Features

* add EF Core outbox store adapter ([28e9ee7](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/28e9ee776a9a254bd434d48f15142efca439f3a4))
* add InMemoryOutboxStore with full IOutboxStore contract ([bce28c3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/bce28c3663562419417535ab63e35d8742c8c23d))
* add OutboxWorkerService with retry and dead-letter, add AddOutbox DI extension ([d121243](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/d12124382682d962d9860ab8ed20ad7ba8d78d45))
* add runtime contracts for ZeroAlloc.Outbox ([423449a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/423449a7e9d1d38925224c6e5a7190d52fce3a6f))
* add SystemTextJsonOutboxSerializer and DefaultOutboxDispatcher ([22c44b7](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/22c44b7efe2fd82a9187952ab02f7f6b52a43546))
* implement OutboxCodeWriter — emit writer, dispatcher, DI extension per [OutboxMessage] ([0fb9038](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/0fb9038aae902a1c0b5f37f857a422fd624b0fd8))
* scaffold OutboxGenerator with model, diagnostics, and TryParse ([fed9d59](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/fed9d591d5946c0aab41150d9b249b78c251bcaf))


### Bug Fixes

* add partial to OutboxServiceCollectionExtensions, fix worker doc and test assertions ([e3c04b8](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/e3c04b883de764d1ed7cc7ea6d53c9779030b3b0))
* captive dependency, unused Polly, dispatcher lifetime, MarkFailed status reset, nested type guard ([c7c4213](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/c7c4213a9cc0d6b289c454528f9eabeaf87dcec2))
* correct FetchPending batch-size guard, add RetryCount assertion, clean up csproj ([7854d37](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/7854d37174d08f953221eefdc06c7330085ac64c))
* de-enlist transaction after EnqueueAsync, remove SQL Server-specific filter predicate ([9b518e9](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/9b518e9c331193cd741b6c07c3895da4fe45d3e5))
* propagate AOT attributes to IOutboxSerializer, freeze options, improve error messages ([b0006d8](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/b0006d8180e613c79c4b29a11210c752693605af))
* use byte[] backing for OutboxEntry.Payload, clarify IOutboxTypeDispatcher doc ([78a6935](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/78a6935b7b574af284f3fcea52d1117a47f92052))
* use count-only DiagnosticsEqual, fuse diagnostic loops in generator ([8f554bd](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/8f554bda0ddbc0b6f085e22b49430c7767826064))
* use single generator run with hermetic references, add [UsesVerify], annotate AOT pragma ([b7f423b](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/commit/b7f423ba43d2b4de98d179bda446d093f90ed5df))
