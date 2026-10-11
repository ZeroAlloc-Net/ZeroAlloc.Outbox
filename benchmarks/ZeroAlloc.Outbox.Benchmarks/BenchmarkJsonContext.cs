using System.Text.Json.Serialization;

namespace ZeroAlloc.Outbox.Benchmarks;

// The Serialisation generator routes the STJ serializers of the benchmark messages through this context.
[JsonSerializable(typeof(SerializedOrderPaid))]
[JsonSerializable(typeof(SerializedOrderShipped))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
