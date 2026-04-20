using System.Threading.Tasks;

namespace ZeroAlloc.Outbox.Generator.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public Task SimpleRecord_WithNamespace_GeneratesWriterAndDispatcher()
        => GeneratorTestHelper.VerifyGenerator("""
            using ZeroAlloc.Outbox;

            namespace MyApp;

            [OutboxMessage]
            public sealed record OrderPlaced(int OrderId, decimal Amount);
            """);

    [Fact]
    public Task GlobalNamespace_GeneratesProxy()
        => GeneratorTestHelper.VerifyGenerator("""
            using ZeroAlloc.Outbox;

            [OutboxMessage]
            public sealed record PingMessage(string Value);
            """);

    [Fact]
    public Task StructType_GeneratesWriterAndDispatcher()
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
}
