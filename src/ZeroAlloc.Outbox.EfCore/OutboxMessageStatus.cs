namespace ZeroAlloc.Outbox.EfCore;

public enum OutboxMessageStatus
{
    Pending = 0,
    Succeeded = 1,
    DeadLetter = 2,
}
