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

    // Alternating measurement rounds of the baseline and the dispatch path; see the Json_Struct test.
    private const int Rounds = 4;

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
        //
        // Both paths run the same System.Text.Json code, and that code reads 40 B more while it is still
        // Tier0 than once it is optimised (208 B against 168 B). AllocationGate can return a reading taken
        // before the tier-up of a multi-method call path has finished (ZeroAlloc.TestHelpers#94), and
        // measuring the baseline first then hands it the unoptimised code and the dispatch path the
        // optimised code. So the two are measured in alternating rounds and each keeps its least reading,
        // which is the optimised one: code that is not yet optimised only ever allocates more.
        var json = long.MaxValue;
        var bytes = long.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            json = Math.Min(json, AllocationGate.MeasureBytesPerCall(Iterations, () =>
                JsonSerializer.Deserialize(payload.Span, EnqueueMessageJsonContext.Default.StructEnqueueMessage)));
            bytes = Math.Min(bytes, AllocationGate.MeasureBytesPerCall(Iterations, () =>
                Complete(typeDispatcher.DispatchAsync(payload, CancellationToken.None))));
        }

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
