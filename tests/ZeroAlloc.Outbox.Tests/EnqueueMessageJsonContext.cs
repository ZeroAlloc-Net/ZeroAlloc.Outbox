using System.Text.Json.Serialization;

namespace ZeroAlloc.Outbox.Tests;

// The Serialisation generator routes the STJ serializers of the enqueue test messages through this context.
[JsonSerializable(typeof(StructEnqueueMessage))]
[JsonSerializable(typeof(ClassEnqueueMessage))]
internal sealed partial class EnqueueMessageJsonContext : JsonSerializerContext;
