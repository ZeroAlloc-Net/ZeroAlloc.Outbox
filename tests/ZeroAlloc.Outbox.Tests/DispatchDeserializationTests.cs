using System.Text.Json;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The read path: the type dispatcher the generator emits, <see cref="DispatchingOutboxSerializer"/> and the
/// <c>SerializerDispatcher</c> that ZeroAlloc.Serialisation generates for this assembly. The message
/// dispatcher only records the message, so what is measured is the deserialization alone.
/// </summary>
/// <remarks>
/// Every call completes synchronously on the test thread, so the allocation counter sees all of it.
/// </remarks>
public sealed class DispatchDeserializationTests
{
    private const int Iterations = 100;

    private static readonly SerializerDispatcher s_dispatcher = new();

    [Fact]
    public void MemoryPack_Struct_Message_Dispatch_Allocates_Nothing()
    {
        var serializer = new DispatchingOutboxSerializer(s_dispatcher);
        var received = new ReceivingDispatcher<MemoryPackStructMessage>();
        IOutboxTypeDispatcher typeDispatcher = new MemoryPackStructMessageOutboxTypeDispatcher(serializer, received);
        var message = new MemoryPackStructMessage { Id = 42, Amount = 1234567890L, Express = true };
        var payload = serializer.Serialize(message);

        var bytes = AllocationGate.MeasureBytesPerCall(Iterations, () =>
            Complete(typeDispatcher.DispatchAsync(payload, CancellationToken.None)));

        received.Last.Should().Be(message);
        bytes.Should().Be(0, "the struct is read back as itself through Deserialize<T>, without a box");
    }

    [Fact]
    public void Json_Struct_Message_Dispatch_Allocates_Only_What_System_Text_Json_Does()
    {
        var serializer = new DispatchingOutboxSerializer(s_dispatcher);
        var received = new ReceivingDispatcher<StructEnqueueMessage>();
        IOutboxTypeDispatcher typeDispatcher = new StructEnqueueMessageOutboxTypeDispatcher(serializer, received);
        var message = new StructEnqueueMessage(42, 1234567890L, true);
        var payload = serializer.Serialize(message);

        // System.Text.Json allocates while it reads a struct with constructor parameters. That cost is the
        // format's own; the outbox must add nothing to it, a box of the struct included.
        var json = AllocationGate.MeasureBytesPerCall(Iterations, () =>
            JsonSerializer.Deserialize(payload.Span, EnqueueMessageJsonContext.Default.StructEnqueueMessage));
        var bytes = AllocationGate.MeasureBytesPerCall(Iterations, () =>
            Complete(typeDispatcher.DispatchAsync(payload, CancellationToken.None)));

        received.Last.Should().Be(message);
        bytes.Should().Be(json, "the struct is read back as itself through Deserialize<T>, without a box");
    }

    private static void Complete(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
            throw new InvalidOperationException("The dispatch was expected to complete synchronously.");
    }

    private sealed class ReceivingDispatcher<T> : IOutboxDispatcher<T> where T : notnull
    {
        public T? Last { get; private set; }

        public ValueTask DispatchAsync(T message, CancellationToken ct)
        {
            Last = message;
            return ValueTask.CompletedTask;
        }
    }
}
