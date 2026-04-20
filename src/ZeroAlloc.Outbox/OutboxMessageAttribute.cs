namespace ZeroAlloc.Outbox;

/// <summary>
/// Marks a type as an outbox message. The source generator emits a typed
/// <see cref="IOutboxWriter{T}"/> and DI registration for each annotated type.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class OutboxMessageAttribute : Attribute { }
