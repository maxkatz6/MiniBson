using System;
using System.Buffers;

namespace MiniBson;

/// <summary>
/// Document lengths handed from a measuring pass to the writing pass that follows it.
/// </summary>
/// <remarks>
/// <para>
/// An implementation detail of generated serializers, and not something application code has
/// reason to construct. Writing a document whose length has to be supplied means measuring it
/// first; measuring each nested document again where it is written costs O(N·depth). The
/// measure pass instead reserves a slot per document as it descends and fills it on the way
/// back up, and the write pass reads those lengths back in the same pre-order, so both passes
/// walk the graph once.
/// </para>
/// <para>
/// The two passes agree by construction: they visit members in the same order under the same
/// conditions. <see cref="Next"/> throws rather than returning a wrong length if they ever
/// stop agreeing.
/// </para>
/// </remarks>
#if MINIBSON_PUBLIC
public sealed class BsonSizeTable
#else
internal sealed class BsonSizeTable
#endif
{
    /// <summary>
    /// A table that records nothing and reports every length as unknown. This is the seekable
    /// destination: the writer patches lengths in afterwards, so measuring buys nothing and
    /// the measure pass is skipped entirely.
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
    /// A table to measure into, or <see cref="None"/> when the destination does not need
    /// lengths supplied.
    /// </summary>
    public static BsonSizeTable Rent(bool active) => active ? new BsonSizeTable(active: true) : None;

    /// <summary>
    /// Whether this table records anything. False for <see cref="None"/>, whose only purpose
    /// is to let the write pass run unmeasured through the same code.
    /// </summary>
    public bool IsActive => _active;

    /// <summary>
    /// Claims the slot for a document about to be descended into, before its own length is
    /// known. Reserving on the way down is what puts the slots in the order the write pass
    /// asks for them.
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

    /// <summary>Fills in a slot claimed by <see cref="Reserve"/>.</summary>
    public void Record(int slot, int size)
    {
        if (_active)
            _sizes[slot] = size;
    }

    /// <summary>
    /// The next recorded length, in the order the measure pass reserved them. Returns 0 for
    /// <see cref="None"/>, which is what <see cref="BsonWriter.WriteStartDocument(int)"/>
    /// reads as "patch it in later".
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
    /// Releases the backing storage. Safe to call on <see cref="None"/> and safe to call
    /// twice, so generated code can put it in a finally block without conditions.
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
