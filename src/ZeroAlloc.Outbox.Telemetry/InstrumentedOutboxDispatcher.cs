using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Telemetry;

/// <summary>
/// Bridges the source-generated <see cref="OutboxDispatcherTelemetryInstrumented"/> proxy
/// (which implements <see cref="IOutboxDispatcherTelemetry"/>) back to the foundational
/// <see cref="IOutboxTypeDispatcher"/> contract that the Outbox worker resolves.
/// </summary>
/// <remarks>
/// <para>
/// The adapter implements both interfaces using explicit interface implementations so
/// callers cast to whichever interface they need. The flow per dispatch:
/// </para>
/// <list type="number">
///   <item><description>Worker calls <see cref="IOutboxTypeDispatcher.DispatchAsync"/> on the adapter.</description></item>
///   <item><description>Adapter forwards to <see cref="OutboxDispatcherTelemetryInstrumented.DispatchAsync"/> (records spans/counters/histograms).</description></item>
///   <item><description>Proxy invokes <see cref="IOutboxDispatcherTelemetry.DispatchAsync"/> back on the adapter.</description></item>
///   <item><description>Adapter forwards to the actual inner <see cref="IOutboxTypeDispatcher.DispatchAsync"/>.</description></item>
/// </list>
/// </remarks>
internal sealed class InstrumentedOutboxDispatcher : IOutboxTypeDispatcher, IOutboxDispatcherTelemetry
{
    private readonly IOutboxTypeDispatcher _inner;
    private readonly OutboxDispatcherTelemetryInstrumented _proxy;

    public InstrumentedOutboxDispatcher(IOutboxTypeDispatcher inner)
    {
        _inner = inner;
        // The proxy wraps `this` (which exposes IOutboxDispatcherTelemetry by forwarding to _inner).
        _proxy = new OutboxDispatcherTelemetryInstrumented(this);
    }

    // IOutboxTypeDispatcher.TypeName: forward to inner dispatcher.
    public string TypeName => _inner.TypeName;

    // IOutboxTypeDispatcher.DispatchAsync: route through the proxy (which records spans/counters/histograms).
    ValueTask IOutboxTypeDispatcher.DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => _proxy.DispatchAsync(payload, ct);

    // IOutboxDispatcherTelemetry.DispatchAsync: invoked by the proxy → calls the actual inner dispatcher.
    ValueTask IOutboxDispatcherTelemetry.DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => _inner.DispatchAsync(payload, ct);
}
