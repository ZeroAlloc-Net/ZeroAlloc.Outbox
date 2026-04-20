using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Default <see cref="IOutboxSerializer"/> backed by System.Text.Json.
/// For AOT/trimming scenarios, supply a source-generated serializer instead.
/// </summary>
[RequiresUnreferencedCode("Reflection-based JSON serialization. For AOT, use a source-generated serializer.")]
[RequiresDynamicCode("Reflection-based JSON serialization. For AOT, use a source-generated serializer.")]
public sealed class SystemTextJsonOutboxSerializer : IOutboxSerializer
{
    private static readonly JsonSerializerOptions s_options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public ReadOnlyMemory<byte> Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, s_options);

    public T Deserialize<T>(ReadOnlyMemory<byte> data)
        => JsonSerializer.Deserialize<T>(data.Span, s_options)
           ?? throw new InvalidOperationException(
               $"Deserialized null for type {typeof(T).FullName ?? typeof(T).Name}.");
}
