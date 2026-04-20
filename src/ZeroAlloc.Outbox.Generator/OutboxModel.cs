namespace ZeroAlloc.Outbox.Generator;

/// <summary>Data model produced by the generator parser, one per [OutboxMessage] type.</summary>
internal sealed class OutboxModel : System.IEquatable<OutboxModel>
{
    public OutboxModel(
        string? ns,
        string typeName,
        string typeFqn,
        bool isInterface,
        bool isStatic,
        System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> diagnostics)
    {
        Namespace = ns;
        TypeName = typeName;
        TypeFqn = typeFqn;
        IsInterface = isInterface;
        IsStatic = isStatic;
        Diagnostics = diagnostics;
    }

    public string? Namespace { get; }
    public string TypeName { get; }
    public string TypeFqn { get; }
    public bool IsInterface { get; }
    public bool IsStatic { get; }
    public System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> Diagnostics { get; }

    public bool Equals(OutboxModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Namespace, other.Namespace, System.StringComparison.Ordinal)
            && string.Equals(TypeName, other.TypeName, System.StringComparison.Ordinal)
            && string.Equals(TypeFqn, other.TypeFqn, System.StringComparison.Ordinal)
            && IsInterface == other.IsInterface
            && IsStatic == other.IsStatic
            && DiagnosticsEqual(Diagnostics, other.Diagnostics);
    }

    public override bool Equals(object? obj) => obj is OutboxModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Namespace != null ? System.StringComparer.Ordinal.GetHashCode(Namespace) : 0;
            hash = hash * 31 + System.StringComparer.Ordinal.GetHashCode(TypeName);
            hash = hash * 31 + System.StringComparer.Ordinal.GetHashCode(TypeFqn);
            hash = hash * 31 + IsInterface.GetHashCode();
            hash = hash * 31 + IsStatic.GetHashCode();
            return hash;
        }
    }

    private static bool DiagnosticsEqual(
        System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> a,
        System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }
}
