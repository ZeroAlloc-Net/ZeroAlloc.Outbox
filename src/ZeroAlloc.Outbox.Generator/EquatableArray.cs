using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ZeroAlloc.Outbox.Generator;

/// <summary>An immutable array compared by its elements.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(IEnumerable<T> items) => _items = ImmutableArray.CreateRange(items);

    private ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public int Count => Items.Length;

    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other)
    {
        var mine = Items.AsSpan();
        var theirs = other.Items.AsSpan();
        if (mine.Length != theirs.Length) return false;
        for (var i = 0; i < mine.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(mine[i], theirs[i])) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Count;
            foreach (var item in Items)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public ImmutableArray<T> AsImmutableArray() => Items;

    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();
}
