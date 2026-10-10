using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>
/// Source-generated JSON metadata for the REST responses, written with the web defaults
/// (camelCase), which is what <c>Results.Ok</c> used before.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OutboxSnapshot))]
[JsonSerializable(typeof(List<ThroughputPoint>))]
[JsonSerializable(typeof(List<PipelineInfo>))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext
{
}
