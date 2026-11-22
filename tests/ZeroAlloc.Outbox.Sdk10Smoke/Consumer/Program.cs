using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.InMemory;
using ZeroAlloc.Outbox.Sdk10Smoke;

// AddOrderPlacedOutbox exists only if the compiler loaded the Outbox generator.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddLogging();
builder.Services
    .AddOutbox()
    .WithInMemoryStore()
    .AddOrderPlacedOutbox();

// Resolving the writer would need a serializer, which is the job of the Serialisation
// generator; this smoke only checks what the Outbox generator registered.
var registered = builder.Services
    .Where(d => d.ServiceType == typeof(IOutboxWriter<OrderPlaced>))
    .ToArray();
if (registered.Length == 0)
{
    Console.Error.WriteLine("FAIL: the generated IOutboxWriter<OrderPlaced> registration is missing");
    return 1;
}

Console.WriteLine("SDK 10 consumer: generator loaded, message writer registered.");
return 0;

namespace ZeroAlloc.Outbox.Sdk10Smoke
{
    [OutboxMessage]
    public sealed record OrderPlaced(string OrderId, decimal Total);
}
