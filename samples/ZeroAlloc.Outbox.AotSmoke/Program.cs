using System;
using System.Threading;
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
// ZeroAlloc.Serialisation dispatcher.

var received = new TaskCompletionSource<OrderPlaced>(TaskCreationOptions.RunContinuationsAsynchronously);

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddLogging();
builder.Services.AddSerializerDispatcher();
builder.Services
    .AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
    .WithInMemoryStore()
    .AddOrderPlacedOutbox();
builder.Services.AddSingleton<IOutboxDispatcher<OrderPlaced>>(new RecordingDispatcher(received));

using var host = builder.Build();

var serializer = host.Services.GetRequiredService<IOutboxSerializer>();
if (serializer is not DispatchingOutboxSerializer)
    return Fail($"expected DispatchingOutboxSerializer, got {serializer.GetType().Name}");

var sent = new OrderPlaced("ord-42", 99.95m);
var scope = host.Services.CreateAsyncScope();
await using (scope.ConfigureAwait(false))
{
    var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await writer.WriteAsync(sent, transaction: null, ct: CancellationToken.None).ConfigureAwait(false);
}

await host.StartAsync().ConfigureAwait(false);
OrderPlaced delivered;
try
{
    delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
}
catch (TimeoutException)
{
    return Fail("the worker did not dispatch the message within 10 seconds");
}
finally
{
    await host.StopAsync().ConfigureAwait(false);
}

if (delivered != sent)
    return Fail($"expected {sent}, got {delivered}");

Console.WriteLine("AOT smoke: PASS");
return 0;

static int Fail(string reason)
{
    Console.Error.WriteLine($"AOT smoke: FAIL - {reason}");
    return 1;
}
