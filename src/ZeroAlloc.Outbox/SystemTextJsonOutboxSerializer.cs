using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Default <see cref="IOutboxSerializer"/> backed by System.Text.Json reflection-based serialization.
/// Not AOT-safe by design; for AOT scenarios supply a source-generated <see cref="IOutboxSerializer"/>.
/// </summary>
[RequiresUnreferencedCode("Uses reflection-based JSON serialization. Supply a source-generated IOutboxSerializer for trim/AOT scenarios.")]
[RequiresDynamicCode("Uses reflection-based JSON serialization. Supply a source-generated IOutboxSerializer for AOT scenarios.")]
public sealed class SystemTextJsonOutboxSerializer : IOutboxSerializer
{
    private static readonly JsonSerializerOptions s_options =
        new(JsonSerializerDefaults.Web);

    public ReadOnlyMemory<byte> Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, s_options);

    public T Deserialize<T>(ReadOnlyMemory<byte> data)
        => JsonSerializer.Deserialize<T>(data.Span, s_options)
           ?? throw new InvalidOperationException(
               $"Deserialized null for type {typeof(T).Name}.");
}
