namespace ZeroAlloc.Outbox.Generator;

/// <summary>Data model produced by the generator parser, one per [OutboxMessage] type.</summary>
internal sealed class OutboxModel : System.IEquatable<OutboxModel>
{
    public OutboxModel(
        string? ns,
        string typeName,
        string typeFqn,
        string hintName,
        string displayName,
        bool isGeneric,
        bool isInterface,
        bool isStatic,
        LocationInfo location,
        EquatableArray<DiagnosticInfo> diagnostics)
    {
        Namespace = ns;
        TypeName = typeName;
        TypeFqn = typeFqn;
        HintName = hintName;
        DisplayName = displayName;
        IsGeneric = isGeneric;
        IsInterface = isInterface;
        IsStatic = isStatic;
        Location = location;
        Diagnostics = diagnostics;
    }

    public string? Namespace { get; }
    public string TypeName { get; }
    public string TypeFqn { get; }

    /// <summary>The hint name of the generated file, from <see cref="HintNames.ForHost"/>.</summary>
    public string HintName { get; }

    /// <summary>The type as a diagnostic shows it, such as <c>App.Envelope&lt;T&gt;</c>.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Whether the type is a generic definition, which gets code per closed construction instead of
    /// code of its own.
    /// </summary>
    public bool IsGeneric { get; }
    public bool IsInterface { get; }
    public bool IsStatic { get; }

    /// <summary>The type's declaration, for diagnostics about it.</summary>
    public LocationInfo Location { get; }
    public EquatableArray<DiagnosticInfo> Diagnostics { get; }

    /// <summary>Whether the generator emits code for this type: no error, and a kind it supports.</summary>
    public bool IsEmitted
    {
        get
        {
            if (IsInterface || IsStatic) return false;
            foreach (var d in Diagnostics)
            {
                if (d.Descriptor.DefaultSeverity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error) return false;
            }
            return true;
        }
    }

    public bool Equals(OutboxModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Namespace, other.Namespace, System.StringComparison.Ordinal)
            && string.Equals(TypeName, other.TypeName, System.StringComparison.Ordinal)
            && string.Equals(TypeFqn, other.TypeFqn, System.StringComparison.Ordinal)
            && string.Equals(HintName, other.HintName, System.StringComparison.Ordinal)
            && string.Equals(DisplayName, other.DisplayName, System.StringComparison.Ordinal)
            && IsGeneric == other.IsGeneric
            && IsInterface == other.IsInterface
            && IsStatic == other.IsStatic
            && Location.Equals(other.Location)
            && Diagnostics.Equals(other.Diagnostics);
    }

    public override bool Equals(object? obj) => obj is OutboxModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Namespace != null ? System.StringComparer.Ordinal.GetHashCode(Namespace) : 0;
            hash = hash * 31 + System.StringComparer.Ordinal.GetHashCode(TypeName);
            hash = hash * 31 + System.StringComparer.Ordinal.GetHashCode(TypeFqn);
            hash = hash * 31 + System.StringComparer.Ordinal.GetHashCode(HintName);
            hash = hash * 31 + IsGeneric.GetHashCode();
            hash = hash * 31 + IsInterface.GetHashCode();
            hash = hash * 31 + IsStatic.GetHashCode();
            return hash;
        }
    }
}
