using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.AotSmoke;
using ZeroAlloc.Outbox.InMemory;

// Exercises the documented registration under PublishAot=true: AddSerializerDispatcher() plus
// AddOutbox(), with no trim or AOT suppression anywhere. A message goes through the
// generator-emitted writer into the in-memory store, and the outbox worker claims it and hands
// it to the generator-emitted type dispatcher, which deserializes it through the
// ZeroAlloc.Serialisation dispatcher. A second message goes into the keyed store of a named
// pipeline, whose own worker dispatches it through the same shared dispatcher.

var received = Channel.CreateUnbounded<OrderPlaced>();

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddLogging();
builder.Services.AddSerializerDispatcher();
builder.Services
    .AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
    .WithInMemoryStore()
    .AddOrderPlacedOutbox();
builder.Services
    .AddOutbox("named", o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
    .WithInMemoryStore();
builder.Services.AddSingleton<IOutboxDispatcher<OrderPlaced>>(new RecordingDispatcher(received.Writer));

using var host = builder.Build();

var serializer = host.Services.GetRequiredService<IOutboxSerializer>();
if (serializer is not DispatchingOutboxSerializer)
    return Fail($"expected DispatchingOutboxSerializer, got {serializer.GetType().Name}");

var sent = new OrderPlaced("ord-42", 99.95m);
var sentNamed = new OrderPlaced("ord-43", 12.50m);
var scope = host.Services.CreateAsyncScope();
await using (scope.ConfigureAwait(false))
{
    var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await writer.WriteAsync(sent, transaction: null, ct: CancellationToken.None).ConfigureAwait(false);

    // The generated writer writes to the default pipeline, so a named pipeline is written through
    // its keyed store, under the type name the generated dispatcher is registered with.
    var typeName = scope.ServiceProvider.GetServices<IOutboxTypeDispatcher>().First().TypeName;
    var namedStore = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("named");
    await namedStore.EnqueueAsync(typeName, serializer.Serialize(sentNamed), transaction: null, CancellationToken.None)
        .ConfigureAwait(false);
}

await host.StartAsync().ConfigureAwait(false);
OrderPlaced[] delivered;
try
{
    delivered = [await ReadAsync().ConfigureAwait(false), await ReadAsync().ConfigureAwait(false)];
}
catch (TimeoutException)
{
    return Fail("the workers did not dispatch both messages within 10 seconds");
}
finally
{
    await host.StopAsync().ConfigureAwait(false);
}

if (!delivered.Contains(sent) || !delivered.Contains(sentNamed))
    return Fail($"expected {sent} and {sentNamed}, got {delivered[0]} and {delivered[1]}");

Console.WriteLine("AOT smoke: PASS");
return 0;

async Task<OrderPlaced> ReadAsync()
    => await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

static int Fail(string reason)
{
    Console.Error.WriteLine($"AOT smoke: FAIL - {reason}");
    return 1;
}
