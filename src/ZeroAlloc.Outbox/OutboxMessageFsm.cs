#pragma warning disable ZSM0003 // Retry self-loop via Fail is intentional — message stays in Retry state after each failed attempt while retries remain

using ZeroAlloc.StateMachine;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Source-generated finite state machine that encodes all valid <see cref="OutboxMessageState"/>
/// transitions. Used by store implementations to validate state mutations instead of ad-hoc
/// status comparisons.
/// </summary>
/// <remarks>
/// Usage pattern — create one instance per transition attempt, seed it with the current state
/// via <see cref="GetState"/>, then call <c>TryFire</c>:
/// <code>
/// var state = OutboxMessageFsm.GetState(isSucceeded, isDeadLetter, retryCount);
/// var fsm = new OutboxMessageFsm(state);
/// if (!fsm.TryFire(OutboxMessageTrigger.Dispatch))
///     throw new InvalidOperationException($"Cannot dispatch message in state {state}.");
/// // fsm.Current == OutboxMessageState.Dispatched
/// </code>
/// The class is <c>partial</c>; the ZeroAlloc.StateMachine generator emits
/// <c>TryFire(OutboxMessageTrigger)</c> and the <c>Current</c> property.
/// </remarks>
[StateMachine(InitialState = nameof(OutboxMessageState.Pending))]
// Pending → Dispatched  (first-attempt success)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Pending, On = OutboxMessageTrigger.Dispatch, To = OutboxMessageState.Dispatched)]
// Pending → Retry       (first attempt failed; retries remain)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Pending, On = OutboxMessageTrigger.Fail, To = OutboxMessageState.Retry)]
// Pending → DeadLetter  (no dispatcher registered — dead-lettered without any retry)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Pending, On = OutboxMessageTrigger.Exhaust, To = OutboxMessageState.DeadLetter)]
// Pending → Cancelled   (operator cancel before first dispatch)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Pending, On = OutboxMessageTrigger.Cancel, To = OutboxMessageState.Cancelled)]
// Retry → Dispatched    (retry succeeded)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Retry, On = OutboxMessageTrigger.Dispatch, To = OutboxMessageState.Dispatched)]
// Retry → Retry         (attempt failed; retries still remain — self-loop, ZSM0003 suppressed)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Retry, On = OutboxMessageTrigger.Fail, To = OutboxMessageState.Retry)]
// Retry → DeadLetter    (all retries exhausted)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Retry, On = OutboxMessageTrigger.Exhaust, To = OutboxMessageState.DeadLetter)]
// Retry → Cancelled     (operator cancel during retry window)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.Retry, On = OutboxMessageTrigger.Cancel, To = OutboxMessageState.Cancelled)]
// DeadLetter → Pending  (operator requeue via dashboard)
[Transition<OutboxMessageState, OutboxMessageTrigger>(From = OutboxMessageState.DeadLetter, On = OutboxMessageTrigger.Requeue, To = OutboxMessageState.Pending)]
[Terminal<OutboxMessageState>(State = OutboxMessageState.Dispatched)]
[Terminal<OutboxMessageState>(State = OutboxMessageState.Cancelled)]
public sealed partial class OutboxMessageFsm
{
    /// <summary>
    /// Initializes a new <see cref="OutboxMessageFsm"/> seeded with the message's current state.
    /// Use <see cref="GetState"/> to derive the logical state from store fields.
    /// </summary>
    public OutboxMessageFsm(OutboxMessageState current)
    {
        _state = current;
    }

    /// <summary>
    /// Returns the logical <see cref="OutboxMessageState"/> for a store entry given its
    /// persisted fields. <paramref name="isSucceeded"/> and <paramref name="isDeadLetter"/>
    /// map from the store's status enum; <paramref name="retryCount"/> distinguishes
    /// <see cref="OutboxMessageState.Pending"/> from <see cref="OutboxMessageState.Retry"/>.
    /// </summary>
    public static OutboxMessageState GetState(bool isSucceeded, bool isDeadLetter, int retryCount) =>
        (isSucceeded, isDeadLetter) switch
        {
            (true, _) => OutboxMessageState.Dispatched,
            (_, true) => OutboxMessageState.DeadLetter,
            _ => retryCount == 0 ? OutboxMessageState.Pending : OutboxMessageState.Retry,
        };
}
