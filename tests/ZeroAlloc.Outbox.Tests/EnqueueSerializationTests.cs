using System.Buffers;
using System.Data.Common;
using System.Runtime.InteropServices;
using ZeroAlloc.Serialisation;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The enqueue path: the writer the generator emits, <see cref="DispatchingOutboxSerializer"/> and the
/// <c>SerializerDispatcher</c> that ZeroAlloc.Serialisation generates for this assembly. The store records
/// the payload it is handed and allocates nothing, so what is measured is the serialization alone.
/// </summary>
/// <remarks>
/// Every call completes synchronously on the test thread, so <see cref="AllocationGate"/>, which
/// counts the calling thread's allocations, sees all of it.
/// </remarks>
public sealed class EnqueueSerializationTests
{
    private const int Iterations = 10;

    private static readonly SerializerDispatcher s_dispatcher = new();

    [Fact]
    public void Struct_Message_Enqueue_Allocates_Only_The_Exact_Size_Payload()
    {
        var store = new CapturingStore();
        var writer = new StructEnqueueMessageOutboxWriter(store, new DispatchingOutboxSerializer(s_dispatcher));
        var message = new StructEnqueueMessage(42, 1234567890L, true);

        var bytes = Measure(() => writer.WriteAsync(message, transaction: null, CancellationToken.None));

        bytes.Should().Be(ArraySize(store.Payload.Length),
            "the struct is serialized without boxing into a reused writer, and copied once into the payload");
    }

    [Fact]
    public void Class_Message_Enqueue_Allocates_Only_The_Exact_Size_Payload()
    {
        var store = new CapturingStore();
        var writer = new ClassEnqueueMessageOutboxWriter(store, new DispatchingOutboxSerializer(s_dispatcher));
        var message = new ClassEnqueueMessage("order-42", 99.95m);

        var bytes = Measure(() => writer.WriteAsync(message, transaction: null, CancellationToken.None));

        bytes.Should().Be(ArraySize(store.Payload.Length),
            "the message is serialized into a reused writer, and copied once into the payload");
    }

    [Fact]
    public void Struct_Payload_Is_Byte_Identical_To_The_Dispatchers_Own_Output()
    {
        var message = new StructEnqueueMessage(42, 1234567890L, true);

        var payload = new DispatchingOutboxSerializer(s_dispatcher).Serialize(message);

        payload.ToArray().Should().Equal(s_dispatcher.Serialize(message, typeof(StructEnqueueMessage)).ToArray());
        new DispatchingOutboxSerializer(s_dispatcher).Deserialize<StructEnqueueMessage>(payload).Should().Be(message);
    }

    [Fact]
    public void Class_Payload_Is_Byte_Identical_To_The_Dispatchers_Own_Output()
    {
        var message = new ClassEnqueueMessage("order-42", 99.95m);

        var payload = new DispatchingOutboxSerializer(s_dispatcher).Serialize(message);

        payload.ToArray().Should().Equal(s_dispatcher.Serialize(message, typeof(ClassEnqueueMessage)).ToArray());
        new DispatchingOutboxSerializer(s_dispatcher).Deserialize<ClassEnqueueMessage>(payload).Should().Be(message);
    }

    [Fact]
    public void A_Payload_That_Outgrows_The_Pooled_Buffer_Is_Byte_Identical()
    {
        // Large enough that the pooled buffer is regrown several times while the JSON is written.
        var message = new ClassEnqueueMessage(new string('x', 200_000) + "é中", 1m);

        var payload = new DispatchingOutboxSerializer(s_dispatcher).Serialize(message);

        payload.ToArray().Should().Equal(s_dispatcher.Serialize(message, typeof(ClassEnqueueMessage)).ToArray());
    }

    [Fact]
    public void Payload_Is_One_Whole_Array_So_A_Store_Can_Bind_It_Without_Copying()
    {
        var payload = new DispatchingOutboxSerializer(s_dispatcher).Serialize(new ClassEnqueueMessage("order-42", 1m));

        MemoryMarshal.TryGetArray(payload, out var segment).Should().BeTrue();
        segment.Offset.Should().Be(0);
        segment.Count.Should().Be(segment.Array!.Length);
    }

    [Fact]
    public void Payloads_Of_Successive_Calls_Do_Not_Share_Or_Keep_Bytes()
    {
        var serializer = new DispatchingOutboxSerializer(s_dispatcher);

        var longer = serializer.Serialize(new ClassEnqueueMessage(new string('x', 500), 1m)).ToArray();
        var first = serializer.Serialize(new ClassEnqueueMessage("a", 1m));
        var second = serializer.Serialize(new ClassEnqueueMessage("b", 2m));

        longer.Length.Should().BeGreaterThan(first.Length);
        first.ToArray().Should().Equal(s_dispatcher.Serialize(new ClassEnqueueMessage("a", 1m), typeof(ClassEnqueueMessage)).ToArray());
        second.ToArray().Should().Equal(s_dispatcher.Serialize(new ClassEnqueueMessage("b", 2m), typeof(ClassEnqueueMessage)).ToArray());
    }

    [Fact]
    public void A_Throwing_Serializer_Leaves_The_Next_Payload_Intact()
    {
        var dispatcher = new ScriptedDispatcher();
        var serializer = new DispatchingOutboxSerializer(dispatcher);

        dispatcher.OnSerialize = writer =>
        {
            Write(writer, [1, 2, 3, 4]);
            throw new InvalidOperationException("boom");
        };
        var act = () => serializer.Serialize(1);
        act.Should().Throw<InvalidOperationException>().WithMessage("boom");

        dispatcher.OnSerialize = writer => Write(writer, [9]);
        serializer.Serialize(1).ToArray().Should().Equal([9], "the bytes the failed call wrote are discarded");
    }

    [Fact]
    public void A_Reentrant_Serialize_On_The_Same_Thread_Gets_A_Writer_Of_Its_Own()
    {
        var dispatcher = new ScriptedDispatcher();
        var serializer = new DispatchingOutboxSerializer(dispatcher);
        ReadOnlyMemory<byte> inner = default;
        var depth = 0;

        dispatcher.OnSerialize = writer =>
        {
            if (depth++ == 0)
            {
                Write(writer, [1, 2]);
                inner = serializer.Serialize(2);
                Write(writer, [3]);
            }
            else
            {
                Write(writer, [7, 7, 7]);
            }
        };

        var outer = serializer.Serialize(1);

        outer.ToArray().Should().Equal([1, 2, 3]);
        inner.ToArray().Should().Equal([7, 7, 7]);
    }

    private static long Measure(Func<ValueTask> enqueue) =>
        AllocationGate.MeasureBytesPerCall(Iterations, () => Complete(enqueue()));

    private static void Complete(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
            throw new InvalidOperationException("The enqueue was expected to complete synchronously.");
    }

    // A byte[] of n elements on a 64-bit runtime: a 24-byte header and the data, padded to 8 bytes.
    private static long ArraySize(int length) => (24 + length + 7) & ~7L;

    private static void Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }

    private sealed class ScriptedDispatcher : ISerializerDispatcher
    {
        public Action<IBufferWriter<byte>> OnSerialize { get; set; } = static _ => { };

        public void Serialize<T>(IBufferWriter<byte> writer, T value) => OnSerialize(writer);

        public ReadOnlyMemory<byte> Serialize(object value, Type type) =>
            throw new InvalidOperationException("The outbox must serialize through the buffer writer overload.");

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type) => null;
    }

    private sealed class CapturingStore : IOutboxStore
    {
        public ReadOnlyMemory<byte> Payload { get; private set; }

        public ValueTask EnqueueAsync(
            string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
        {
            Payload = payload;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
