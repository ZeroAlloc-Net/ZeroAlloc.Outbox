using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.Outbox.Generator;

/// <summary>
/// The names the generator derives from a closed generic message type: the name stored with each
/// outbox row, the reference the generated code uses, and the identifier its generated types are
/// named after.
/// </summary>
internal static class TypeNames
{
    /// <summary>
    /// The name stored in the TypeName column: the fully qualified C# name of the runtime type,
    /// without <c>global::</c>, such as <c>App.Envelope&lt;App.Order&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Types that are one runtime type get one name: a keyword such as <c>int</c> is written as
    /// <c>System.Int32</c>, <c>dynamic</c> as <c>System.Object</c>, a tuple as its
    /// <c>System.ValueTuple</c>, and nullable reference annotations are dropped. A top-level
    /// non-generic type gets the same name as its own stored name, the namespace and the type name
    /// joined by a dot. Nested types are joined to their containing type by a dot, as in C#.
    /// </remarks>
    public static string Stored(ITypeSymbol type)
    {
        var sb = new StringBuilder();
        AppendStored(sb, type);
        return sb.ToString();
    }

    /// <summary>
    /// A reference to the type for the generated code: fully qualified with <c>global::</c>, and with
    /// every identifier that is a keyword escaped, so it binds to the same type wherever it appears.
    /// </summary>
    public static string Reference(ITypeSymbol type)
    {
        var sb = new StringBuilder();
        AppendReference(sb, type);
        return sb.ToString();
    }

    /// <summary>
    /// The identifier a closed generic type's generated names are built from, as ZeroAlloc.Serialisation
    /// builds them: its name, then <c>Of</c> and its type arguments joined by <c>And</c>, each built the
    /// same way. <c>Envelope&lt;Order&gt;</c> gives <c>EnvelopeOfOrder</c> and
    /// <c>Pair&lt;int, Order&gt;</c> gives <c>PairOfInt32AndOrder</c>. Type arguments of containing
    /// types come first.
    /// </summary>
    public static string Identifier(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return Identifier(array.ElementType) + (array.Rank == 1 ? "Array" : $"Array{array.Rank}D");
            case IDynamicTypeSymbol:
                return "Object";
            case INamedTypeSymbol named:
                named = Underlying(named);
                var arguments = TypeArgumentsInScope(named);
                if (arguments.Count == 0) return named.Name;
                var sb = new StringBuilder(named.Name).Append("Of");
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (i > 0) sb.Append("And");
                    sb.Append(Identifier(arguments[i]));
                }
                return sb.ToString();
            default:
                return type.Name;
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a concrete type the generated code can name: no type
    /// parameter, pointer or unresolved type anywhere in it.
    /// </summary>
    public static bool IsClosed(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsClosed(array.ElementType);
            case IDynamicTypeSymbol:
                return true;
            case INamedTypeSymbol named:
                if (named.TypeKind == TypeKind.Error || named.IsUnboundGenericType) return false;
                var arguments = TypeArgumentsInScope(Underlying(named));
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (!IsClosed(arguments[i])) return false;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The first type in <paramref name="type"/> that generated code elsewhere in the assembly cannot
    /// name, because it is private, protected or file-local, or <see langword="null"/> when there is none.
    /// </summary>
    public static ITypeSymbol? FirstInaccessible(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return FirstInaccessible(array.ElementType);
            case INamedTypeSymbol named:
                named = Underlying(named);
                for (var current = named; current is not null; current = current.ContainingType)
                {
                    if (current.IsFileLocal || current.DeclaredAccessibility is
                            Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                        return current;
                }
                var arguments = TypeArgumentsInScope(named);
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (FirstInaccessible(arguments[i]) is { } inaccessible) return inaccessible;
                }
                return null;
            default:
                return null;
        }
    }

    private static void AppendStored(StringBuilder sb, ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                AppendStored(sb, array.ElementType);
                AppendRank(sb, array.Rank);
                return;
            case IDynamicTypeSymbol:
                sb.Append("System.Object");
                return;
            case INamedTypeSymbol named:
                named = Underlying(named);
                if (named.ContainingType is { } outer)
                {
                    AppendStored(sb, outer);
                    sb.Append('.');
                }
                else if (!named.ContainingNamespace.IsGlobalNamespace)
                {
                    // The same namespace text as the stored name of a non-generic message.
                    sb.Append(named.ContainingNamespace.ToDisplayString()).Append('.');
                }
                sb.Append(named.Name);
                AppendArguments(sb, named, AppendStored);
                return;
            default:
                sb.Append(type.ToDisplayString());
                return;
        }
    }

    private static void AppendReference(StringBuilder sb, ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                AppendReference(sb, array.ElementType);
                AppendRank(sb, array.Rank);
                return;
            case IDynamicTypeSymbol:
                sb.Append("global::System.Object");
                return;
            case INamedTypeSymbol named:
                named = Underlying(named);
                if (named.ContainingType is { } outer)
                {
                    AppendReference(sb, outer);
                    sb.Append('.');
                }
                else
                {
                    sb.Append("global::");
                    AppendNamespace(sb, named.ContainingNamespace);
                }
                sb.Append(Escape(named.Name));
                AppendArguments(sb, named, AppendReference);
                return;
            default:
                sb.Append(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                return;
        }
    }

    private static void AppendNamespace(StringBuilder sb, INamespaceSymbol ns)
    {
        if (ns.IsGlobalNamespace) return;
        AppendNamespace(sb, ns.ContainingNamespace);
        sb.Append(Escape(ns.Name)).Append('.');
    }

    private static void AppendArguments(
        StringBuilder sb, INamedTypeSymbol named, System.Action<StringBuilder, ITypeSymbol> append)
    {
        if (named.TypeArguments.Length == 0) return;
        sb.Append('<');
        for (var i = 0; i < named.TypeArguments.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            append(sb, named.TypeArguments[i]);
        }
        sb.Append('>');
    }

    private static void AppendRank(StringBuilder sb, int rank)
        => sb.Append('[').Append(',', rank - 1).Append(']');

    private static string Escape(string identifier)
        => SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;

    /// <summary>A tuple type is its <c>System.ValueTuple</c>; element names are not part of the runtime type.</summary>
    private static INamedTypeSymbol Underlying(INamedTypeSymbol named)
        => named.IsTupleType && named.TupleUnderlyingType is { } underlying ? underlying : named;

    /// <summary>The type arguments of <paramref name="type"/> and its containing types, outermost first.</summary>
    private static List<ITypeSymbol> TypeArgumentsInScope(INamedTypeSymbol type)
    {
        var arguments = new List<ITypeSymbol>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var own = current.TypeArguments;
            for (var i = own.Length - 1; i >= 0; i--)
                arguments.Insert(0, own[i]);
        }
        return arguments;
    }
}
