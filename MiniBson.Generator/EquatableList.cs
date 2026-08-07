using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MiniBson.Generator;

/// <summary>
/// A list with value equality, so a record holding one takes part in the incremental
/// pipeline's cache comparison instead of falling back to reference equality.
/// </summary>
internal sealed class EquatableList<T>(IList<T> collection)
    : ReadOnlyCollection<T>(collection), IEquatable<EquatableList<T>>
{
    public static readonly EquatableList<T> Empty = new([]);

    public bool Equals(EquatableList<T>? other)
    {
        if (other is null || Count != other.Count)
            return false;

        for (var i = 0; i < Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(this[i], other[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    /// <summary>
    /// Order- and length-sensitive, unlike a plain XOR: property order decides element order
    /// on the wire, so two models differing only in it are genuinely different models.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = Count;
        for (var i = 0; i < Count; i++)
            hash = hash * -1521134295 + (this[i]?.GetHashCode() ?? 0);

        return hash;
    }

    public static bool operator ==(EquatableList<T>? left, EquatableList<T>? right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    public static bool operator !=(EquatableList<T>? left, EquatableList<T>? right) =>
        !(left == right);
}
