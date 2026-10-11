using System.Text.Json.Serialization;

namespace ZeroAlloc.Outbox.AotSmoke.Adapters;

// The Serialisation generator routes the STJ serializer through this source-generated
// context, so no reflection-based JSON is involved.
[JsonSerializable(typeof(OrderShipped))]
[JsonSerializable(typeof(OrderPaid))]
internal sealed partial class MessagesJsonContext : JsonSerializerContext;
