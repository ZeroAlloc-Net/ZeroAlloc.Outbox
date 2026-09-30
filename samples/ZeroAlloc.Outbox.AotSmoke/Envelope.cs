using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.AotSmoke;
using ZeroAlloc.Serialisation;

// A closed generic message needs an AOT-safe serializer of its own, declared on the assembly.
// The Outbox generator finds Envelope<OrderPlaced> through the IOutboxWriter of it in Program.cs,
// so it needs no [assembly: OutboxMessage(typeof(...))] declaration here.
[assembly: ZeroAllocSerializable(typeof(Envelope<OrderPlaced>), SerializationFormat.SystemTextJson)]

namespace ZeroAlloc.Outbox.AotSmoke;

// [OutboxMessage] on a generic type makes the generator emit a writer and a type dispatcher for
// every closed construction it can see, and AddEnvelopeOutbox() that registers them all.
[OutboxMessage]
public sealed record Envelope<T>(string CorrelationId, T Payload);
