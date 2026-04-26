using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Telemetry;

namespace ZeroAlloc.Outbox.Telemetry;

/// <summary>
/// Marker interface picked up by ZeroAlloc.Telemetry's [Instrument] source generator.
/// The generator emits <c>OutboxDispatcherTelemetryInstrumented</c> implementing this
/// interface; the proxy wraps an instance and records spans, counters, and histograms
/// on every <see cref="DispatchAsync"/> call.
/// </summary>
/// <remarks>
/// This interface intentionally does NOT inherit from <c>IOutboxTypeDispatcher</c> — the
/// source generator only walks members declared directly on the [Instrument] interface,
/// so an inheriting design produced an incomplete proxy (missing TypeName) and a ctor
/// that wouldn't accept the foundational dispatcher type. A small adapter (in this
/// package, written in Task 1.4) bridges the generated proxy back to <c>IOutboxTypeDispatcher</c>.
/// </remarks>
[Instrument("ZeroAlloc.Outbox")]
public interface IOutboxDispatcherTelemetry
{
    [Trace("outbox.dispatch")]
    [Count("outbox.dispatched_total")]
    [Histogram("outbox.dispatch_duration_ms")]
    ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}
