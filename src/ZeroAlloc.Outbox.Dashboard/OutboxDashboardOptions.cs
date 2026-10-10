using System.Text.Json.Serialization.Metadata;

namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>Settings for <c>MapOutboxDashboard</c>.</summary>
public sealed class OutboxDashboardOptions
{
    /// <summary>Base path for all dashboard endpoints. The default is <c>/outbox</c>.</summary>
    public string BasePath { get; set; } = "/outbox";

    /// <summary>
    /// Resolves the JSON type info of the host's own <see cref="OutboxDashboardEvent"/> subclasses,
    /// so the SSE stream can write them without reflection, which NativeAOT does not allow. Use a
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> with a
    /// <c>[JsonSerializable]</c> attribute per event type, created with default
    /// <see cref="System.Text.Json.JsonSerializerOptions"/>, so the frames match the built-in ones,
    /// which are PascalCase. A custom event whose type is not registered is skipped, with one logged
    /// warning per type, and the stream continues.
    /// </summary>
    public IJsonTypeInfoResolver? EventTypeInfoResolver { get; set; }
}
