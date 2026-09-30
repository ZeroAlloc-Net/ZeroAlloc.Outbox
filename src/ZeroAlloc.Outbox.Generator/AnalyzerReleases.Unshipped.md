; Unshipped analyzer release.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category         | Severity | Notes
--------|------------------|----------|-------------------------------------------------------------
ZO0004  | ZeroAlloc.Outbox | Warning  | Generic [OutboxMessage] type has no closed usage
ZO0005  | ZeroAlloc.Outbox | Error    | Stored type name is longer than 256 characters
ZO0006  | ZeroAlloc.Outbox | Error    | [OutboxMessage] declaration of a closed generic type is invalid
ZO0007  | ZeroAlloc.Outbox | Error    | Outbox message would get the same generated names as another message
