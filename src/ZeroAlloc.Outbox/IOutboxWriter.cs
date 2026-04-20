using System.Data.Common;

namespace ZeroAlloc.Outbox;

/// <summary>Writes a single outbox message to the durable store.</summary>
public interface IOutboxWriter<T> where T : notnull
{
    /// <summary>
    /// Enqueues <paramref name="message"/> in the outbox store.
    /// Pass <paramref name="transaction"/> to enlist in an ambient DB transaction.
    /// </summary>
    ValueTask WriteAsync(
        T message,
        DbTransaction? transaction = null,
        CancellationToken ct = default);
}
