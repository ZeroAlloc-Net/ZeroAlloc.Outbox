using System.Threading.Tasks;

namespace ZeroAlloc.Outbox.Generator.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void SimpleRecord_WithNamespace_GeneratesWriterAndDispatcher()
        => GeneratorTestHelper.VerifyGenerator("""
            using ZeroAlloc.Outbox;

            namespace MyApp;

            [OutboxMessage]
            public sealed record OrderPlaced(int OrderId, decimal Amount);
            """);

    [Fact]
    public void GlobalNamespace_GeneratesProxy()
        => GeneratorTestHelper.VerifyGenerator("""
            using ZeroAlloc.Outbox;

            [OutboxMessage]
            public sealed record PingMessage(string Value);
            """);

    [Fact]
    public void StructType_GeneratesWriterAndDispatcher()
        => GeneratorTestHelper.VerifyGenerator("""
            using ZeroAlloc.Outbox;

            namespace MyApp;

            [OutboxMessage]
            public readonly struct StockReserved
            {
                public int ProductId { get; init; }
                public int Quantity { get; init; }
            }
            """);

    [Fact]
    public void ClosedGenerics_GenerateOneWriterAndDispatcherEach()
        => GeneratorTestHelper.VerifyGenerator("""
            using ZeroAlloc.Outbox;

            [assembly: OutboxMessage(typeof(MyApp.Envelope<MyApp.OrderPlaced>))]

            namespace MyApp;

            [OutboxMessage]
            public sealed record Envelope<T>(string CorrelationId, T Payload);

            public sealed record OrderPlaced(int OrderId);
            public sealed record StockReserved(int ProductId);

            public sealed class Reservations(IOutboxWriter<Envelope<StockReserved>> writer)
            {
                public IOutboxWriter<Envelope<StockReserved>> Writer { get; } = writer;
            }
            """);
}
