using System;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Outbox.Generator;

/// <summary>
/// The source location of a declaration or usage, kept in a model so a diagnostic about it can be
/// reported where it is, and suppressed there with <c>#pragma</c>.
/// </summary>
/// <remarks>
/// Two locations are equal when they are in the same syntax tree at the same span. An edit to a
/// file gives it a new syntax tree, so a model from that file compares unequal and its output is
/// produced again with the new location, instead of being served from the cache with the old one.
/// </remarks>
internal readonly struct LocationInfo : IEquatable<LocationInfo>
{
    private readonly Location? _location;

    private LocationInfo(Location location) => _location = location;

    public static LocationInfo From(Location? location) => new(location ?? Location.None);

    public Location ToLocation() => _location ?? Location.None;

    /// <summary>Whether this location comes before <paramref name="other"/> in source order: by file, then by position.</summary>
    public bool IsBefore(LocationInfo other)
    {
        var mine = ToLocation();
        var theirs = other.ToLocation();
        var byFile = string.CompareOrdinal(mine.SourceTree?.FilePath, theirs.SourceTree?.FilePath);
        return byFile != 0 ? byFile < 0 : mine.SourceSpan.Start < theirs.SourceSpan.Start;
    }

    public bool Equals(LocationInfo other) => ToLocation().Equals(other.ToLocation());

    public override bool Equals(object? obj) => obj is LocationInfo other && Equals(other);

    public override int GetHashCode() => ToLocation().GetHashCode();
}
