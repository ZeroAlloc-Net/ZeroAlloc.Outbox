using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.EfCore;

/// <summary>EF Core entity that maps to the OutboxMessages table.</summary>
// TODO(#outbox-dashboard): add [Timestamp] byte[] RowVersion to detect
// Requeue/Cancel/ForceDispatch races against the dispatcher worker.
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
}
