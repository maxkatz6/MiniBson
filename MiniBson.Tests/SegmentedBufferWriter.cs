using System.Buffers;

namespace MiniBson.Tests;

/// <summary>
/// A difficult destination. It gives exactly <c>segmentSize</c> bytes, or exactly the size hint
/// when the hint asks for more.
/// </summary>
/// <remarks>
/// <para>
/// This is the least that the contract allows. <see cref="IBufferWriter{T}"/> promises only that
/// a buffer holds the requested number of bytes or more. A real <c>PipeWriter</c> acts this way
/// at a segment boundary. An <c>ArrayBufferWriter</c> never does, so it hides the difference.
/// </para>
/// <para>
/// With <c>segmentSize: 1</c> the writer gets one byte at a time. Thus each value longer than one
/// byte must cross buffers. Each buffer is a new array, and this class drops it after
/// <see cref="Advance"/>. Thus a writer that uses an old buffer loses those bytes and does not
/// appear to work.
/// </para>
/// </remarks>
internal sealed class SegmentedBufferWriter(int segmentSize) : IBufferWriter<byte>
{
    private readonly List<byte> _written = [];
    private byte[]? _current;

    public byte[] WrittenBytes => [.. _written];

    public int WrittenCount => _written.Count;

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        // segmentSize is 1 or more. Thus a hint of 0 still gets a buffer that is not empty.
        _current = new byte[Math.Max(segmentSize, sizeHint)];
        return _current;
    }

    public void Advance(int count)
    {
        if (_current is null)
            throw new InvalidOperationException("Advance without a preceding GetSpan or GetMemory.");

        if (count < 0 || count > _current.Length)
        {
            throw new InvalidOperationException(
                $"Advance({count}) is outside the {_current.Length} bytes that were handed out.");
        }

        _written.AddRange(_current.AsSpan(0, count));
        _current = null;
    }
}
