using System;

namespace ZeroAlloc.Outbox.Generator;

/// <summary>
/// A closed construction of a generic message type the generator found: an IOutboxWriter or
/// IOutboxDispatcher of it in the source, or an assembly-level [OutboxMessage(typeof ...)].
/// </summary>
/// <remarks>
/// It holds strings and values only, so the incremental pipeline can compare it between runs.
/// A usage that cannot be generated carries <see cref="Error"/> instead of generated names.
/// </remarks>
internal sealed class ClosedMessage : IEquatable<ClosedMessage>
{
    public ClosedMessage(
        string? definitionKey,
        string displayName,
        string storedName,
        string reference,
        string identifier,
        LocationInfo location,
        DiagnosticInfo? error)
    {
        DefinitionKey = definitionKey;
        DisplayName = displayName;
        StoredName = storedName;
        Reference = reference;
        Identifier = identifier;
        Location = location;
        Error = error;
    }

    public static ClosedMessage Invalid(string? definitionKey, DiagnosticInfo error)
        => new(definitionKey, string.Empty, string.Empty, string.Empty, string.Empty, error.Location, error);

    /// <summary>The hint name of the generic definition, which identifies it, or null when there is none.</summary>
    public string? DefinitionKey { get; }

    /// <summary>The closed type as a diagnostic shows it, such as <c>App.Envelope&lt;App.Order&gt;</c>.</summary>
    public string DisplayName { get; }

    /// <summary>The name stored with each outbox row, from <see cref="TypeNames.Stored"/>.</summary>
    public string StoredName { get; }

    /// <summary>The type as the generated code refers to it, from <see cref="TypeNames.Reference"/>.</summary>
    public string Reference { get; }

    /// <summary>The identifier the generated type names are built from, from <see cref="TypeNames.Identifier"/>.</summary>
    public string Identifier { get; }

    /// <summary>Where the usage or declaration is, for diagnostics about it.</summary>
    public LocationInfo Location { get; }

    /// <summary>Why this usage cannot be generated, or null when it can.</summary>
    public DiagnosticInfo? Error { get; }

    public bool Equals(ClosedMessage? other)
        => other is not null
            && string.Equals(DefinitionKey, other.DefinitionKey, StringComparison.Ordinal)
            && string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)
            && string.Equals(StoredName, other.StoredName, StringComparison.Ordinal)
            && string.Equals(Reference, other.Reference, StringComparison.Ordinal)
            && string.Equals(Identifier, other.Identifier, StringComparison.Ordinal)
            && Location.Equals(other.Location)
            && Equals(Error, other.Error);

    public override bool Equals(object? obj) => Equals(obj as ClosedMessage);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = DefinitionKey is null ? 0 : StringComparer.Ordinal.GetHashCode(DefinitionKey);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(StoredName);
            hash = hash * 31 + Location.GetHashCode();
            return hash;
        }
    }
}
