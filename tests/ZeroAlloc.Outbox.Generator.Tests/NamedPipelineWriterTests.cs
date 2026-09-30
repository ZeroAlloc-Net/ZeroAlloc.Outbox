using System.Collections.Concurrent;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.Generator.Tests;

/// <summary>
/// The generated <c>Add{Name}Outbox</c> on a named pipeline's builder registers a writer keyed by
/// the pipeline's name that writes to that pipeline's store, ZeroAlloc.Outbox#256. On the default
/// builder it registers what it always did. The generated code is compiled, loaded and run.
/// </summary>
public sealed class NamedPipelineWriterTests
{
    private const string Source = """
        using ZeroAlloc.Outbox;

        namespace App;

        [OutboxMessage]
        public sealed record OrderPlaced(int OrderId);
        """;

    private static readonly Lazy<Assembly> s_generated = new(Compile);

    [Fact]
    public async Task On_A_Named_Builder_The_Writer_Is_Keyed_And_Writes_To_The_Pipelines_Store()
    {
        var (services, defaultStore, workflowStore) = NewServices();

        AddOrderPlacedOutbox(services.AddOutbox("workflow", _ => { }));

        using var sp = services.BuildServiceProvider();
        sp.GetService(WriterType).Should().BeNull("a named pipeline registers no unkeyed writer");
        await WriteAsync(sp.GetRequiredKeyedService(WriterType, "workflow"), 7);

        workflowStore.TypeNames.Should().Equal("App.OrderPlaced");
        defaultStore.TypeNames.Should().BeEmpty();
    }

    [Fact]
    public async Task On_The_Default_Builder_The_Writer_Is_Unchanged()
    {
        var (services, defaultStore, workflowStore) = NewServices();

        AddOrderPlacedOutbox(services.AddOutbox());

        using var sp = services.BuildServiceProvider();
        sp.GetKeyedService(WriterType, "workflow").Should().BeNull();
        await WriteAsync(sp.GetRequiredService(WriterType), 7);

        defaultStore.TypeNames.Should().Equal("App.OrderPlaced");
        workflowStore.TypeNames.Should().BeEmpty();
    }

    [Fact]
    public async Task On_Both_Builders_Each_Writer_Targets_Its_Own_Pipeline_And_The_Dispatcher_Is_Registered_Once()
    {
        var (services, defaultStore, workflowStore) = NewServices();

        AddOrderPlacedOutbox(services.AddOutbox());
        AddOrderPlacedOutbox(services.AddOutbox("workflow", _ => { }));
        AddOrderPlacedOutbox(services.AddOutbox("workflow", _ => { }));

        services.Count(d => d.ServiceType == typeof(IOutboxTypeDispatcher)).Should().Be(1);
        services.Count(d => d.ServiceType == WriterType).Should().Be(2, "one unkeyed and one keyed writer");

        using var sp = services.BuildServiceProvider();
        await WriteAsync(sp.GetRequiredService(WriterType), 1);
        await WriteAsync(sp.GetRequiredKeyedService(WriterType, "workflow"), 2);

        defaultStore.TypeNames.Should().ContainSingle();
        workflowStore.TypeNames.Should().ContainSingle();
    }

    private static System.Type MessageType => s_generated.Value.GetType("App.OrderPlaced", throwOnError: true)!;

    private static System.Type WriterType => typeof(IOutboxWriter<>).MakeGenericType(MessageType);

    private static (ServiceCollection Services, RecordingStore Default, RecordingStore Workflow) NewServices()
    {
        var services = new ServiceCollection();
        var defaultStore = new RecordingStore();
        var workflowStore = new RecordingStore();
        services.AddSingleton<IOutboxStore>(defaultStore);
        services.AddKeyedSingleton<IOutboxStore>("workflow", workflowStore);
        services.AddSingleton<IOutboxSerializer>(new NullSerializer());
        return (services, defaultStore, workflowStore);
    }

    private static void AddOrderPlacedOutbox(IOutboxBuilder builder)
        => s_generated.Value.GetType("App.OutboxServiceCollectionExtensions", throwOnError: true)!
            .GetMethod("AddOrderPlacedOutbox")!
            .Invoke(null, [builder]);

    private static async Task WriteAsync(object writer, int orderId)
    {
        var message = System.Activator.CreateInstance(MessageType, orderId);
        var write = WriterType.GetMethod(nameof(IOutboxWriter<object>.WriteAsync))!;
        await ((ValueTask)write.Invoke(writer, [message, null, CancellationToken.None])!).ConfigureAwait(false);
    }

    private static Assembly Compile()
    {
        GeneratorTestHelper.RunCompiled(Source, out var output);
        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        emitted.Success.Should().BeTrue(string.Join('\n', emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
        return Assembly.Load(stream.ToArray());
    }

    private sealed class NullSerializer : IOutboxSerializer
    {
        public System.ReadOnlyMemory<byte> Serialize<T>(T value) => new byte[] { 1 };

        public T Deserialize<T>(System.ReadOnlyMemory<byte> data) => throw new System.NotSupportedException();
    }

    private sealed class RecordingStore : IOutboxStore
    {
        public ConcurrentQueue<string> TypeNames { get; } = new();

        public ValueTask EnqueueAsync(
            string typeName, System.ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
        {
            TypeNames.Enqueue(typeName);
            return ValueTask.CompletedTask;
        }

        public ValueTask<System.Collections.Generic.IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
            int batchSize, OutboxLease lease, CancellationToken ct) => throw new System.NotSupportedException();

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new System.NotSupportedException();

        public ValueTask<int> ReleaseLeasesAsync(
            System.Collections.Generic.IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => throw new System.NotSupportedException();

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new System.NotSupportedException();

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, System.DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => throw new System.NotSupportedException();

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => throw new System.NotSupportedException();
    }
}
