using System;

namespace ZeroAlloc.Outbox.AotSmoke.Adapters;

/// <summary>A failed check; Program reports its message and exits with 1.</summary>
internal sealed class SmokeFailure(string message) : Exception(message);
