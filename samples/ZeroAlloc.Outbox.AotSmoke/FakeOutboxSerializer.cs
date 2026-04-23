using System;
using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

// Fixed-shape serializer that never touches reflection — the interface annotations
// (RequiresUnreferencedCode / RequiresDynamicCode) propagate to any caller, so the
// writer emits suppression. Implementers that stay reflection-free (like this one)
// are safe in practice under AOT.
internal sealed class FakeOutboxSerializer : IOutboxSerializer
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Fake serializer has no reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Fake serializer has no reflection.")]
    public ReadOnlyMemory<byte> Serialize<T>(T value)
        => System.Text.Encoding.UTF8.GetBytes(value?.ToString() ?? "");

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Fake serializer has no reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Fake serializer has no reflection.")]
    public T Deserialize<T>(ReadOnlyMemory<byte> data) => default!;
}
