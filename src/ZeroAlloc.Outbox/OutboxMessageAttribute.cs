namespace ZeroAlloc.Outbox;

/// <summary>
/// Marks a type as an outbox message. The source generator emits a typed
/// <see cref="IOutboxWriter{T}"/>, an <see cref="IOutboxTypeDispatcher"/> and a DI registration
/// for each annotated type.
/// </summary>
/// <remarks>
/// <para>
/// Apply <see cref="OutboxMessageAttribute()"/> to a top-level class, record or struct.
/// </para>
/// <para>
/// A generic message type such as <c>Envelope&lt;T&gt;</c> is marked the same way, and the
/// generator emits code for each closed construction it can see: every
/// <c>IOutboxWriter&lt;Envelope&lt;Order&gt;&gt;</c> or <c>IOutboxDispatcher&lt;Envelope&lt;Order&gt;&gt;</c>
/// in the source, and every assembly-level declaration made with
/// <see cref="OutboxMessageAttribute(System.Type)"/>:
/// <c>[assembly: OutboxMessage(typeof(Envelope&lt;Order&gt;))]</c>. The generated code names the
/// closed type directly, so no reflection or runtime code generation is involved and it stays
/// NativeAOT-safe.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class OutboxMessageAttribute : Attribute
{
    /// <summary>Marks the class, record or struct this attribute is applied to as an outbox message.</summary>
    public OutboxMessageAttribute()
    {
    }

    /// <summary>
    /// Declares the closed generic type <paramref name="type"/> an outbox message. Valid only on the
    /// assembly, for a closed construction of a generic type marked <c>[OutboxMessage]</c> in the same
    /// assembly: <c>[assembly: OutboxMessage(typeof(Envelope&lt;Order&gt;))]</c>.
    /// </summary>
    /// <param name="type">A closed construction of a generic type, such as <c>typeof(Envelope&lt;Order&gt;)</c>.</param>
    public OutboxMessageAttribute(Type type)
    {
        Type = type;
    }

    /// <summary>
    /// The closed generic type an assembly-level declaration names, or <see langword="null"/> when
    /// the attribute is applied to a type declaration.
    /// </summary>
    public Type? Type { get; }
}
