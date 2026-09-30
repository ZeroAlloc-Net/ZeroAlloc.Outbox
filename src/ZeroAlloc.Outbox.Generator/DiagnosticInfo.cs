using System;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Outbox.Generator;

/// <summary>A diagnostic as values, reported when the pipeline emits.</summary>
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    public DiagnosticInfo(DiagnosticDescriptor descriptor, LocationInfo location, params string[] arguments)
    {
        Descriptor = descriptor;
        Location = location;
        Arguments = new EquatableArray<string>(arguments);
    }

    public DiagnosticDescriptor Descriptor { get; }
    public LocationInfo Location { get; }
    public EquatableArray<string> Arguments { get; }

    public Diagnostic ToDiagnostic()
    {
        var arguments = new object[Arguments.Count];
        for (var i = 0; i < arguments.Length; i++) arguments[i] = Arguments[i];
        return Diagnostic.Create(Descriptor, Location.ToLocation(), arguments);
    }

    public bool Equals(DiagnosticInfo? other)
        => other is not null
            && string.Equals(Descriptor.Id, other.Descriptor.Id, StringComparison.Ordinal)
            && Location.Equals(other.Location)
            && Arguments.Equals(other.Arguments);

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    public override int GetHashCode()
        => unchecked(StringComparer.Ordinal.GetHashCode(Descriptor.Id) * 31 + Location.GetHashCode());
}
