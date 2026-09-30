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
// ZeroAlloc.Serialisation dispatcher. A second message goes through the generated writer keyed
// by a named pipeline into that pipeline's store, whose own worker dispatches it through the same
// shared dispatcher. A third is a closed
// generic message, Envelope<OrderPlaced>, which round-trips under the stored name
// "ZeroAlloc.Outbox.AotSmoke.Envelope<ZeroAlloc.Outbox.AotSmoke.OrderPlaced>" with no reflection.

var received = Channel.CreateUnbounded<OrderPlaced>();
var receivedEnvelopes = Channel.CreateUnbounded<Envelope<OrderPlaced>>();
const string EnvelopeTypeName = "ZeroAlloc.Outbox.AotSmoke.Envelope<ZeroAlloc.Outbox.AotSmoke.OrderPlaced>";

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddLogging();
builder.Services.AddSerializerDispatcher();
builder.Services
    .AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
    .WithInMemoryStore()
    .AddOrderPlacedOutbox()
    .AddEnvelopeOutbox();
builder.Services
    .AddOutbox("named", o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
    .WithInMemoryStore()
    .AddOrderPlacedOutbox();
builder.Services.AddSingleton<IOutboxDispatcher<OrderPlaced>>(new RecordingDispatcher(received.Writer));
builder.Services.AddSingleton<IOutboxDispatcher<Envelope<OrderPlaced>>>(
    new EnvelopeRecordingDispatcher(receivedEnvelopes.Writer));

using var host = builder.Build();

var serializer = host.Services.GetRequiredService<IOutboxSerializer>();
if (serializer is not DispatchingOutboxSerializer)
    return Fail($"expected DispatchingOutboxSerializer, got {serializer.GetType().Name}");

var sent = new OrderPlaced("ord-42", 99.95m);
var sentNamed = new OrderPlaced("ord-43", 12.50m);
var sentEnvelope = new Envelope<OrderPlaced>("corr-44", new OrderPlaced("ord-44", 7.25m));
var scope = host.Services.CreateAsyncScope();
await using (scope.ConfigureAwait(false))
{
    var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await writer.WriteAsync(sent, transaction: null, ct: CancellationToken.None).ConfigureAwait(false);

    var envelopeWriter = scope.ServiceProvider.GetRequiredService<IOutboxWriter<Envelope<OrderPlaced>>>();
    await envelopeWriter.WriteAsync(sentEnvelope, transaction: null, ct: CancellationToken.None).ConfigureAwait(false);

    var dispatchers = scope.ServiceProvider.GetServices<IOutboxTypeDispatcher>().Select(d => d.TypeName).ToArray();
    if (!dispatchers.Contains(EnvelopeTypeName))
        return Fail($"expected a type dispatcher for {EnvelopeTypeName}, got {string.Join(", ", dispatchers)}");
    if (dispatchers.Length != 2)
        return Fail($"expected the shared OrderPlaced and Envelope dispatchers once each, got {string.Join(", ", dispatchers)}");

    // The generated writer registered on the named pipeline's builder is keyed by its name and
    // writes to that pipeline's store.
    var namedWriter = scope.ServiceProvider.GetRequiredKeyedService<IOutboxWriter<OrderPlaced>>("named");
    await namedWriter.WriteAsync(sentNamed, transaction: null, ct: CancellationToken.None).ConfigureAwait(false);
    if (scope.ServiceProvider.GetRequiredKeyedService<InMemoryOutboxStore>("named").AllEntries().Count != 1)
        return Fail("the keyed writer did not write to the named pipeline's store");
}

await host.StartAsync().ConfigureAwait(false);
OrderPlaced[] delivered;
Envelope<OrderPlaced> deliveredEnvelope;
try
{
    delivered = [await ReadAsync().ConfigureAwait(false), await ReadAsync().ConfigureAwait(false)];
    deliveredEnvelope = await receivedEnvelopes.Reader.ReadAsync().AsTask()
        .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
}
catch (TimeoutException)
{
    return Fail("the workers did not dispatch all three messages within 10 seconds");
}
finally
{
    await host.StopAsync().ConfigureAwait(false);
}

if (!delivered.Contains(sent) || !delivered.Contains(sentNamed))
    return Fail($"expected {sent} and {sentNamed}, got {delivered[0]} and {delivered[1]}");

if (deliveredEnvelope != sentEnvelope)
    return Fail($"expected {sentEnvelope}, got {deliveredEnvelope}");

Console.WriteLine("AOT smoke: PASS");
return 0;

async Task<OrderPlaced> ReadAsync()
    => await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

static int Fail(string reason)
{
    Console.Error.WriteLine($"AOT smoke: FAIL - {reason}");
    return 1;
}
