using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Outbox.Generator;

/// <summary>
/// Emits one file per generic message type, with a writer and a type dispatcher for each of its
/// closed constructions and one <c>Add{Name}Outbox</c> that registers them all.
/// </summary>
internal static class ClosedGenericEmitter
{
    /// <summary>The length of the TypeName column every outbox store keeps the stored name in.</summary>
    internal const int MaxStoredNameLength = 256;

    private const string GlobalNamespace = "<global namespace>";

    public static void Emit(
        SourceProductionContext ctx,
        ImmutableArray<OutboxModel> models,
        ImmutableArray<ClosedMessage> closedMessages)
    {
        var definitions = Distinct(models);
        var used = ReportInvalid(ctx, closedMessages);
        var byDefinition = FirstOfEach(closedMessages);

        var generics = new List<Generic>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (!definition.IsGeneric || !definition.IsEmitted) continue;

            if (!used.Contains(definition.HintName))
            {
                Report(ctx, OutboxDiagnostics.OpenGenericWithoutClosedUsage, definition.Location,
                    definition.DisplayName, definition.TypeName);
                continue;
            }

            if (RegistrationOwner(definitions, definition) is { } owner)
            {
                Report(ctx, OutboxDiagnostics.GeneratedNameCollision, definition.Location,
                    definition.DisplayName, $"Add{definition.TypeName}Outbox", owner.DisplayName,
                    definition.Namespace ?? GlobalNamespace);
                continue;
            }

            byDefinition.TryGetValue(definition.HintName, out var found);
            generics.Add(new Generic(definition, WithinLength(ctx, found)));
        }

        RemoveNameCollisions(ctx, definitions, generics);

        for (var i = 0; i < generics.Count; i++)
        {
            var generic = generics[i];
            if (generic.Messages.Count > 0)
                OutboxCodeWriter.WriteClosedGenerics(ctx, generic.Definition, generic.Messages);
        }
    }

    /// <summary>
    /// The definitions once each, in hint-name order. A partial type declared with attributes in
    /// several places is parsed once per declaration.
    /// </summary>
    private static List<OutboxModel> Distinct(ImmutableArray<OutboxModel> models)
    {
        var byHintName = new SortedDictionary<string, OutboxModel>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (!byHintName.ContainsKey(model.HintName)) byHintName[model.HintName] = model;
        }
        return new List<OutboxModel>(byHintName.Values);
    }

    /// <summary>
    /// Reports the error of every usage or declaration that cannot be generated, and returns the
    /// definitions that have a closed usage at all, valid or not.
    /// </summary>
    private static HashSet<string> ReportInvalid(SourceProductionContext ctx, ImmutableArray<ClosedMessage> closedMessages)
    {
        var reported = new HashSet<DiagnosticInfo>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in closedMessages)
        {
            if (message.DefinitionKey is { } key) used.Add(key);
            if (message.Error is { } error && reported.Add(error))
                ctx.ReportDiagnostic(error.ToDiagnostic());
        }
        return used;
    }

    /// <summary>
    /// The valid closed constructions of each definition, once per stored name, at the usage that
    /// comes first in source order, sorted by stored name.
    /// </summary>
    private static Dictionary<string, List<ClosedMessage>> FirstOfEach(ImmutableArray<ClosedMessage> closedMessages)
    {
        var first = new Dictionary<(string Definition, string StoredName), ClosedMessage>();
        foreach (var message in closedMessages)
        {
            if (message.Error is not null || message.DefinitionKey is not { } definition) continue;
            var key = (definition, message.StoredName);
            if (!first.TryGetValue(key, out var existing) || message.Location.IsBefore(existing.Location))
                first[key] = message;
        }

        var byDefinition = new Dictionary<string, List<ClosedMessage>>(StringComparer.Ordinal);
        foreach (var pair in first)
        {
            if (!byDefinition.TryGetValue(pair.Key.Definition, out var list))
                byDefinition[pair.Key.Definition] = list = new List<ClosedMessage>();
            list.Add(pair.Value);
        }
        foreach (var list in byDefinition.Values)
            list.Sort(static (a, b) => string.CompareOrdinal(a.StoredName, b.StoredName));
        return byDefinition;
    }

    /// <summary>The messages whose stored name fits the TypeName column; ZO0005 for the others.</summary>
    private static List<ClosedMessage> WithinLength(SourceProductionContext ctx, List<ClosedMessage>? found)
    {
        var messages = new List<ClosedMessage>();
        if (found is null) return messages;
        for (var i = 0; i < found.Count; i++)
        {
            var message = found[i];
            if (message.StoredName.Length > MaxStoredNameLength)
            {
                Report(ctx, OutboxDiagnostics.StoredTypeNameTooLong, message.Location,
                    message.DisplayName, message.StoredName.Length.ToString(CultureInfo.InvariantCulture));
                continue;
            }
            messages.Add(message);
        }
        return messages;
    }

    /// <summary>
    /// Another emitted message in the same namespace whose registration method would also be
    /// <c>Add{Name}Outbox</c>, such as a non-generic <c>Foo</c> next to <c>Foo&lt;T&gt;</c>.
    /// </summary>
    private static OutboxModel? RegistrationOwner(List<OutboxModel> definitions, OutboxModel definition)
    {
        for (var i = 0; i < definitions.Count; i++)
        {
            var other = definitions[i];
            if (other.IsEmitted
                && !string.Equals(other.HintName, definition.HintName, StringComparison.Ordinal)
                && string.Equals(other.Namespace, definition.Namespace, StringComparison.Ordinal)
                && string.Equals(other.TypeName, definition.TypeName, StringComparison.Ordinal))
                return other;
        }
        return null;
    }

    /// <summary>
    /// Drops every closed construction whose generated type names another message in the same
    /// namespace also gets, and reports ZO0007 for it. A non-generic message keeps its names.
    /// </summary>
    private static void RemoveNameCollisions(
        SourceProductionContext ctx, List<OutboxModel> definitions, List<Generic> generics)
    {
        // (namespace, identifier) -> the messages whose generated types are named after it.
        var owners = new Dictionary<(string Namespace, string Identifier), List<string>>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var model = definitions[i];
            if (model.IsEmitted && !model.IsGeneric)
                Add(owners, (model.Namespace ?? string.Empty, model.TypeName), model.DisplayName);
        }
        for (var i = 0; i < generics.Count; i++)
        {
            var ns = generics[i].Definition.Namespace ?? string.Empty;
            var messages = generics[i].Messages;
            for (var j = 0; j < messages.Count; j++)
                Add(owners, (ns, messages[j].Identifier), messages[j].DisplayName);
        }

        for (var i = 0; i < generics.Count; i++)
        {
            var generic = generics[i];
            var ns = generic.Definition.Namespace ?? string.Empty;
            generic.Messages.RemoveAll(message =>
            {
                var names = owners[(ns, message.Identifier)];
                if (names.Count == 1) return false;
                var other = string.Equals(names[0], message.DisplayName, StringComparison.Ordinal) ? names[1] : names[0];
                Report(ctx, OutboxDiagnostics.GeneratedNameCollision, message.Location,
                    message.DisplayName, message.Identifier + "OutboxWriter", other,
                    ns.Length == 0 ? GlobalNamespace : ns);
                return true;
            });
        }
    }

    private static void Add(
        Dictionary<(string Namespace, string Identifier), List<string>> owners,
        (string Namespace, string Identifier) key,
        string displayName)
    {
        if (!owners.TryGetValue(key, out var names))
            owners[key] = names = new List<string>();
        names.Add(displayName);
    }

    private static void Report(
        SourceProductionContext ctx, DiagnosticDescriptor descriptor, LocationInfo location, params string[] arguments)
        => ctx.ReportDiagnostic(new DiagnosticInfo(descriptor, location, arguments).ToDiagnostic());

    private sealed class Generic
    {
        public Generic(OutboxModel definition, List<ClosedMessage> messages)
        {
            Definition = definition;
            Messages = messages;
        }

        public OutboxModel Definition { get; }
        public List<ClosedMessage> Messages { get; }
    }
}
