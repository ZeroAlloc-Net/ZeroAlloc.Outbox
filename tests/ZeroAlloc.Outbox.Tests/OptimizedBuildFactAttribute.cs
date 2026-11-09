using System.Diagnostics;
using System.Reflection;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// A fact that only means something against optimised code, and is skipped otherwise.
/// </summary>
/// <remarks>
/// Without compiler optimisation an async method gets a class for its state machine instead of a
/// struct, so every call of it allocates, whatever the code does. An allocation gate on a Debug
/// build then measures the compiler's output, not the library. CI builds Release, which is the
/// build the figures are pinned for.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OptimizedBuildFactAttribute : FactAttribute
{
    public OptimizedBuildFactAttribute()
    {
        var debuggable = typeof(OutboxEntry).Assembly.GetCustomAttribute<DebuggableAttribute>();
        if (debuggable is { IsJITOptimizerDisabled: true })
            Skip = "Allocation figures are pinned for optimised code; this build is not optimised. Run with -c Release.";
    }
}
