using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MiniBson.Generator;

/// <summary>
/// A list with value equality. Thus a record that holds one is part of the cache comparison in
/// the incremental pipeline. Without this class, the comparison uses reference equality.
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
    /// This hash code changes with the order and the length, but a simple XOR does not. The
    /// property order sets the element order on the wire. Thus two models with a different
    /// property order are two different models.
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
