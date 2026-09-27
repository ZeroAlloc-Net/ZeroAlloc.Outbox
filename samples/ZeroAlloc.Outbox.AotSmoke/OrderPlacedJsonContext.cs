using System.Text.Json.Serialization;

namespace ZeroAlloc.Outbox.AotSmoke;

// The Serialisation generator routes the STJ serializer through this source-generated
// context, so no reflection-based JSON is involved.
[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class OrderPlacedJsonContext : JsonSerializerContext;
