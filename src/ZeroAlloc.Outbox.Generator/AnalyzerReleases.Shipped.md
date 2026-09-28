; Shipped analyzer releases.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 0.1.0

### New Rules

Rule ID | Category         | Severity | Notes
--------|------------------|----------|-------------------------------
ZO0001  | ZeroAlloc.Outbox | Warning  | [OutboxMessage] on interface
ZO0002  | ZeroAlloc.Outbox | Warning  | [OutboxMessage] on static class
ZO0003  | ZeroAlloc.Outbox | Warning  | [OutboxMessage] on nested type
