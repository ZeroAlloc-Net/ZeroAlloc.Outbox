namespace ZeroAlloc.Outbox;

/// <summary>Trigger that drives an <see cref="OutboxMessageFsm"/> state transition.</summary>
public enum OutboxMessageTrigger
{
    /// <summary>Dispatch succeeded.</summary>
    Dispatch,

    /// <summary>Dispatch failed but retries remain.</summary>
    Fail,

    /// <summary>All retries are exhausted; move to dead-letter.</summary>
    Exhaust,

    /// <summary>Operator cancellation before dispatch completes.</summary>
    Cancel,

    /// <summary>Operator requeue from dead-letter back to pending.</summary>
    Requeue,
}
