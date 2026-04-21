namespace ZeroAlloc.Outbox;

/// <summary>One time bucket of dispatched/failed counts for the throughput chart.</summary>
public sealed record ThroughputPoint(DateTimeOffset Bucket, int Dispatched, int Failed);
