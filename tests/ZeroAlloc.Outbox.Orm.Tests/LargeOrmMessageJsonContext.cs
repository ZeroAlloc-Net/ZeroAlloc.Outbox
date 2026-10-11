using System.Text.Json.Serialization;

namespace ZeroAlloc.Outbox.Orm.Tests;

// The Serialisation generator routes the STJ serializer of LargeOrmMessage through this context.
[JsonSerializable(typeof(LargeOrmMessage))]
internal sealed partial class LargeOrmMessageJsonContext : JsonSerializerContext;
