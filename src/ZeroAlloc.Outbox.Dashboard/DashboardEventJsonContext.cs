using System.Text.Json.Serialization;

namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>
/// Source-generated JSON metadata for the SSE event frames, written with the default options
/// (PascalCase), which is what the frames used before.
/// </summary>
[JsonSerializable(typeof(MessageQueuedEvent))]
[JsonSerializable(typeof(MessageDispatchedEvent))]
[JsonSerializable(typeof(MessageFailedEvent))]
[JsonSerializable(typeof(MessageDeadLetteredEvent))]
[JsonSerializable(typeof(MessageRequeuedEvent))]
[JsonSerializable(typeof(MessageCancelledEvent))]
internal sealed partial class DashboardEventJsonContext : JsonSerializerContext
{
}
