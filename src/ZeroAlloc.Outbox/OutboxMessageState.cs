namespace ZeroAlloc.Outbox;

/// <summary>Logical lifecycle state of an outbox message.</summary>
public enum OutboxMessageState
{
    /// <summary>Message has been enqueued but not yet attempted.</summary>
    Pending,

    /// <summary>At least one dispatch attempt has failed; more retries remain.</summary>
    Retry,

    /// <summary>Message was dispatched successfully.</summary>
    Dispatched,

    /// <summary>All retry attempts were exhausted; message is in the dead-letter queue.</summary>
    DeadLetter,

    /// <summary>Message was cancelled by an operator before dispatch.</summary>
    Cancelled,
}
