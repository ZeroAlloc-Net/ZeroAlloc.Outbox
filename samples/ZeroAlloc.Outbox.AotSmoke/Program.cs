using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.AotSmoke;

// Exercise the generator-emitted OrderPlacedOutboxWriter under PublishAot=true.
// Directly instantiate with fake store + serializer (writer is internal but
// we're in the same namespace). The DI extension AddOrderPlacedOutbox is
// still part of the compilation — ILC analyses it even when unused.

var store = new FakeOutboxStore();
var serializer = new FakeOutboxSerializer();
var writer = new OrderPlacedOutboxWriter(store, serializer);

await writer.WriteAsync(
    new OrderPlaced("ord-42", 99.95m),
    transaction: null,
    ct: CancellationToken.None).ConfigureAwait(false);

if (store.Recorded.Count != 1)
{
    Console.Error.WriteLine($"AOT smoke: FAIL — expected 1 recorded enqueue, got {store.Recorded.Count}");
    return 1;
}

if (!string.Equals(store.Recorded[0].TypeName, "ZeroAlloc.Outbox.AotSmoke.OrderPlaced", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"AOT smoke: FAIL — TypeName expected 'ZeroAlloc.Outbox.AotSmoke.OrderPlaced', got '{store.Recorded[0].TypeName}'");
    return 1;
}

Console.WriteLine("AOT smoke: PASS");
return 0;
