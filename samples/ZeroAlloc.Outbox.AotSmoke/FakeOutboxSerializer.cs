using System;
using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

// IOutboxSerializer annotates Serialize/Deserialize with Requires{Unreferenced,Dynamic}Code;
// every implementation must repeat the annotations (IL2046 / IL3051) even when it uses no
// reflection. Callers (the generator-emitted writer) suppress the resulting diagnostics via
// UnconditionalSuppressMessage on their own methods.
internal sealed class FakeOutboxSerializer : IOutboxSerializer
{
    [RequiresUnreferencedCode("Interface contract requires the annotation; this impl has no reflection.")]
    [RequiresDynamicCode("Interface contract requires the annotation; this impl has no reflection.")]
    public ReadOnlyMemory<byte> Serialize<T>(T value)
        => System.Text.Encoding.UTF8.GetBytes(value?.ToString() ?? "");

    [RequiresUnreferencedCode("Interface contract requires the annotation; this impl has no reflection.")]
    [RequiresDynamicCode("Interface contract requires the annotation; this impl has no reflection.")]
    public T Deserialize<T>(ReadOnlyMemory<byte> data) => default!;
}
