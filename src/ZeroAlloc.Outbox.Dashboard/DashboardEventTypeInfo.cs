using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>
/// Finds the source-generated JSON metadata for the event types the SSE stream writes: the
/// built-in events first, then the resolver the host supplied for its own event types.
/// </summary>
internal sealed class DashboardEventTypeInfo
{
    private static readonly Action<ILogger, string, Exception?> LogUnregistered = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "UnregisteredDashboardEvent"),
        "The dashboard skips events of type '{EventType}' because their JSON type info is not registered. "
        + "Register it by setting OutboxDashboardOptions.EventTypeInfoResolver.");

    private readonly JsonSerializerOptions _options;
    private readonly ConcurrentDictionary<Type, bool> _warned = new();

    public DashboardEventTypeInfo(IJsonTypeInfoResolver? hostResolver)
    {
        // Default options, so the frames match the built-in ones: PascalCase.
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = hostResolver is null
                ? DashboardEventJsonContext.Default
                : JsonTypeInfoResolver.Combine(DashboardEventJsonContext.Default, hostResolver),
        };
        _options.MakeReadOnly();
    }

    /// <summary>The metadata for <paramref name="eventType"/>, or null (after one warning per type) when none is registered.</summary>
    public JsonTypeInfo? Resolve(HttpContext ctx, Type eventType)
    {
        if (_options.TryGetTypeInfo(eventType, out var typeInfo))
            return typeInfo;

        // Mark the type only once the warning is written, so a missing logger does not lose it for good.
        var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ZeroAlloc.Outbox.Dashboard");
        if (logger is not null && _warned.TryAdd(eventType, true))
            LogUnregistered(logger, eventType.FullName ?? eventType.Name, null);
        return null;
    }
}
