namespace ZeroAlloc.Outbox;

/// <summary>A pending outbox message fetched from the store for dispatch.</summary>
public sealed class OutboxEntry
{
    public required Guid Id { get; init; }
    public required string TypeName { get; init; }
    public required byte[] RawPayload { get; init; }
    public ReadOnlyMemory<byte> Payload => RawPayload;
    public required int RetryCount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
