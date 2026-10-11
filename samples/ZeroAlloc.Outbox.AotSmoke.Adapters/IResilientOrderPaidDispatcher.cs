using ZeroAlloc.Outbox;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Outbox.AotSmoke.Adapters;

// The Resilience generator emits IResilientOrderPaidDispatcherResilienceProxy, which wraps the
// registered IResilientOrderPaidDispatcher, and AddResilientOrderPaidDispatcherResiliencePolicies().
[Retry(MaxAttempts = 3, BackoffMs = 10)]
public interface IResilientOrderPaidDispatcher : IOutboxDispatcher<OrderPaid>;
