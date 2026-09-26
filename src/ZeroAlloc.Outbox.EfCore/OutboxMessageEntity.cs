using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.EfCore;

/// <summary>EF Core entity that maps to the OutboxMessages table.</summary>
// TODO(#200): the dashboard's Requeue/Cancel/ForceDispatch check the state and write it in
// separate steps, so they can race a worker's mark. Fix that there, for example with a
// conditional update or a [Timestamp] RowVersion.
[Table("OutboxMessages")]
public sealed class OutboxMessageEntity
{
    [Key]
    public OutboxMessageId Id { get; set; } = OutboxMessageId.New();

    [Required, MaxLength(256)]
    public string TypeName { get; set; } = string.Empty;

    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;

    public int RetryCount { get; set; }

    public DateTimeOffset NextRetryAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? DeadLetterError { get; set; }

    /// <summary>The host currently holding the claim on this row, or null if unleased.</summary>
    public string? LockedBy { get; set; }

    /// <summary>When the current claim expires, or null if unleased.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}
