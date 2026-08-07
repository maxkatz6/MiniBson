using System;
using System.Buffers;

namespace MiniBson;

/// <summary>
/// The document lengths that the measure pass gives to the write pass after it.
/// </summary>
/// <remarks>
/// <para>
/// Generated serializers use this class. Application code must not use it directly. Some
/// documents need a known length before the writer can start them. Thus MiniBson must measure
/// the document first. A second measurement of each nested document at the point where the
/// writer writes it would cost O(N·depth). The measure pass prevents that cost. It keeps a slot
/// for each document when it goes down the object graph, and it fills that slot when it comes
/// back up. The write pass then reads the lengths in the same order, so each pass reads the
/// object graph one time.
/// </para>
/// <para>
/// The two passes always agree, because they read the members in the same order under the same
/// conditions. If they do not agree, <see cref="Next"/> throws an exception instead of a wrong
/// length.
/// </para>
/// </remarks>
#if MINIBSON_PUBLIC
public sealed class BsonSizeTable
#else
internal sealed class BsonSizeTable
#endif
{
    /// <summary>
    /// A table that records nothing and gives each length as unknown. Use it for a destination
    /// that can seek. There the writer writes each length later, so a measurement gives no
    /// advantage and the measure pass does not run.
    /// </summary>
    public static readonly BsonSizeTable None = new(active: false);

    private readonly bool _active;
    private int[] _sizes;
    private int _count;
    private int _cursor;

    private BsonSizeTable(bool active)
    {
        _active = active;
        _sizes = active ? ArrayPool<int>.Shared.Rent(16) : [];
    }

    /// <summary>
    /// A table for the measure pass, or <see cref="None"/> when the destination needs no length
    /// from the caller.
    /// </summary>
    public static BsonSizeTable Rent(bool active) => active ? new BsonSizeTable(active: true) : None;

    /// <summary>
    /// True when this table records the lengths. It is false for <see cref="None"/>. The one
    /// purpose of <see cref="None"/> is to let the write pass use the same code with no
    /// measurement.
    /// </summary>
    public bool IsActive => _active;

    /// <summary>
    /// Keeps the slot for the next document, before the measure pass knows the length of that
    /// document. The measure pass keeps each slot when it goes down the object graph. Thus the
    /// slots are in the order that the write pass asks for.
    /// </summary>
    public int Reserve()
    {
        if (!_active)
            return -1;

        if (_count == _sizes.Length)
            Grow();

        _sizes[_count] = 0;
        return _count++;
    }

    /// <summary>Fills in a slot from <see cref="Reserve"/>.</summary>
    public void Record(int slot, int size)
    {
        if (_active)
            _sizes[slot] = size;
    }

    /// <summary>
    /// The next length, in the order that the measure pass kept the slots. For
    /// <see cref="None"/>, this method returns 0.
    /// <see cref="BsonWriter.WriteStartDocument(int)"/> reads 0 as an unknown length and writes
    /// the length later.
    /// </summary>
    public int Next()
    {
        if (!_active)
            return 0;

        if (_cursor >= _count)
        {
            throw new InvalidOperationException(
                "The write pass asked for more document lengths than the measure pass recorded. " +
                "The two passes disagree about the shape of the value being serialized.");
        }

        return _sizes[_cursor++];
    }

    /// <summary>
    /// Releases the memory. You can call this method on <see cref="None"/>, and you can call it
    /// two times. Thus generated code can put it in a finally block with no test.
    /// </summary>
    public void Return()
    {
        if (!_active || _sizes.Length == 0)
            return;

        var sizes = _sizes;
        _sizes = [];
        _count = 0;
        _cursor = 0;
        ArrayPool<int>.Shared.Return(sizes);
    }

    private void Grow()
    {
        // Math.Max, not doubling alone: Return() leaves _sizes empty, and a table reserved into
        // after that would otherwise rent nothing and index past the end of it.
        var larger = ArrayPool<int>.Shared.Rent(Math.Max(16, _sizes.Length * 2));
        Array.Copy(_sizes, larger, _count);
        ArrayPool<int>.Shared.Return(_sizes);
        _sizes = larger;
    }
}
