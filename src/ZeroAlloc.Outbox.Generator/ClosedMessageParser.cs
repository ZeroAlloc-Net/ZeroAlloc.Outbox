using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Outbox.Generator;

/// <summary>Finds the closed constructions of generic message types the generator emits code for.</summary>
internal static class ClosedMessageParser
{
    /// <summary>
    /// A closed construction used as the type argument of an <c>IOutboxWriter&lt;T&gt;</c> or
    /// <c>IOutboxDispatcher&lt;T&gt;</c>, such as <c>IOutboxWriter&lt;Envelope&lt;Order&gt;&gt;</c>.
    /// A usage that is still open, such as <c>IOutboxWriter&lt;Envelope&lt;T&gt;&gt;</c> in a
    /// generic method, is not one.
    /// </summary>
    public static ClosedMessage? ParseUsage(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not INamedTypeSymbol usage
            || !IsOutboxInterface(usage)
            || usage.TypeArguments[0] is not INamedTypeSymbol message
            || message.TypeArguments.Length == 0
            || message.ContainingType is not null)
            return null;

        var definition = message.OriginalDefinition;
        if (OutboxGenerator.FindOutboxMessageAttribute(definition) is null
            || !SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, ctx.SemanticModel.Compilation.Assembly)
            || !TypeNames.IsClosed(message))
            return null;

        return Create(message, definition, LocationInfo.From(ctx.Node.GetLocation()));
    }

    /// <summary>
    /// The closed constructions declared with <c>[assembly: OutboxMessage(typeof(Envelope&lt;Order&gt;))]</c>
    /// in one compilation unit, and a diagnostic for every declaration that is not one.
    /// </summary>
    public static EquatableArray<ClosedMessage> ParseDeclarations(
        GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var messages = new List<ClosedMessage>();
        foreach (var attribute in ctx.Attributes)
        {
            ct.ThrowIfCancellationRequested();
            var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation());

            if (attribute.ConstructorArguments.Length == 0)
            {
                messages.Add(Invalid(null, location,
                    "[assembly: OutboxMessage] names no type; declare a closed generic type with [assembly: OutboxMessage(typeof(Envelope<Order>))], or mark a type declaration with [OutboxMessage]"));
                continue;
            }

            // A missing or unresolved type is a compiler error already.
            if (attribute.ConstructorArguments[0].Value is not ITypeSymbol type || type.TypeKind == TypeKind.Error)
                continue;

            if (type is not INamedTypeSymbol named || named.TypeArguments.Length == 0)
            {
                messages.Add(Invalid(null, location,
                    $"[assembly: OutboxMessage] names '{type.ToDisplayString()}', which is not generic; the assembly form declares closed generic types, so mark the type declaration with [OutboxMessage] instead"));
                continue;
            }

            var definition = named.OriginalDefinition;
            var definitionKey = HintNames.ForHost(definition);

            if (named.IsUnboundGenericType)
            {
                messages.Add(Invalid(definitionKey, location,
                    $"[assembly: OutboxMessage] names open generic type '{named.ToDisplayString()}'; name a closed construction such as typeof({named.Name}<Order>)"));
                continue;
            }

            if (OutboxGenerator.FindOutboxMessageAttribute(definition) is null)
            {
                messages.Add(Invalid(null, location,
                    $"[assembly: OutboxMessage] names '{named.ToDisplayString()}', but '{definition.ToDisplayString()}' is not marked [OutboxMessage]; mark the generic type declaration with [OutboxMessage]"));
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, ctx.SemanticModel.Compilation.Assembly))
            {
                messages.Add(Invalid(null, location,
                    $"[assembly: OutboxMessage] names '{named.ToDisplayString()}', but '{definition.ToDisplayString()}' is declared in assembly '{definition.ContainingAssembly.Name}'; declare its closed constructions in that assembly"));
                continue;
            }

            // A nested definition gets ZO0003 and no code.
            if (definition.ContainingType is not null || !TypeNames.IsClosed(named))
                continue;

            messages.Add(Create(named, definition, location));
        }
        return new EquatableArray<ClosedMessage>(messages);
    }

    private static ClosedMessage Create(INamedTypeSymbol message, INamedTypeSymbol definition, LocationInfo location)
    {
        var definitionKey = HintNames.ForHost(definition);

        if (TypeNames.FirstInaccessible(message) is { } inaccessible)
        {
            return Invalid(definitionKey, location,
                $"'{message.ToDisplayString()}' cannot be an outbox message: '{inaccessible.ToDisplayString()}' is not accessible from the rest of the assembly, so the generated writer and dispatcher cannot name it");
        }

        return new ClosedMessage(
            definitionKey,
            message.ToDisplayString(),
            TypeNames.Stored(message),
            TypeNames.Reference(message),
            TypeNames.Identifier(message),
            location,
            error: null);
    }

    private static ClosedMessage Invalid(string? definitionKey, LocationInfo location, string message)
        => ClosedMessage.Invalid(definitionKey, new DiagnosticInfo(OutboxDiagnostics.InvalidClosedGeneric, location, message));

    private static bool IsOutboxInterface(INamedTypeSymbol type)
        => type.Arity == 1
            && type.Name is "IOutboxWriter" or "IOutboxDispatcher"
            && string.Equals(type.ContainingNamespace.ToDisplayString(), "ZeroAlloc.Outbox", System.StringComparison.Ordinal);
}
