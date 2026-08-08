using System;
using System.Buffers;

namespace MiniBson;

/// <summary>
/// A growable <see cref="IBufferWriter{T}"/> over a pooled array.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BsonWriter"/> writes to any <see cref="IBufferWriter{T}"/>. This class is the one
/// that MiniBson supplies. It exists because <c>netstandard2.0</c> has no
/// <c>ArrayBufferWriter</c>, and a caller there would have no destination.
/// </para>
/// <para>
/// Give the constructor the length from <c>GetSerializedSize</c>. This class then rents one array
/// of that length. It does not grow, and it makes no copy:
/// </para>
/// <code>
/// using var output = new BsonBufferWriter(context.GetSerializedSize(person));
/// context.Serialize(person, new BsonWriter(output));
/// socket.Send(output.WrittenSpan);
/// </code>
/// </remarks>
#if MINIBSON_PUBLIC
public sealed class BsonBufferWriter : IBufferWriter<byte>, IDisposable
#else
internal sealed class BsonBufferWriter : IBufferWriter<byte>, IDisposable
#endif
{
    private const int DefaultCapacity = 256;

    private byte[] _buffer;
    private int _written;

    /// <param name="initialCapacity">
    /// The length to rent first. A value from <c>GetSerializedSize</c> makes the writer rent one
    /// time.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    public BsonBufferWriter(int initialCapacity = DefaultCapacity)
    {
        if (initialCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialCapacity), initialCapacity, "The capacity cannot be negative.");
        }

        // Rent(0) is allowed to give an array of length zero, and GetSpan must always give one
        // byte or more. Thus this class rents one byte at the least.
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 1));
    }

    /// <summary>The number of bytes written.</summary>
    public int WrittenCount => _written;

    /// <summary>
    /// The length of the rented array. It is the capacity from the constructor or more, because
    /// the pool gives an array of that length or a longer one.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// The bytes written. A later write, a call to <see cref="Clear"/>, or a call to
    /// <see cref="Dispose"/> makes this span invalid.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => new(_buffer, 0, _written);

    /// <summary>
    /// The bytes written. A later write, a call to <see cref="Clear"/>, or a call to
    /// <see cref="Dispose"/> makes this memory invalid.
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => new(_buffer, 0, _written);

    /// <inheritdoc/>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <inheritdoc/>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="count"/> is past the end of the buffer that the last call gave.
    /// </exception>
    public void Advance(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "The count cannot be negative.");

        if (_written + count > _buffer.Length)
        {
            throw new InvalidOperationException(
                $"Advance({count}) is past the end of the buffer, which holds {_buffer.Length - _written} more bytes.");
        }

        _written += count;
    }

    /// <summary>
    /// Discards the bytes written and keeps the array. Use it to write a second document without
    /// a second rental.
    /// </summary>
    public void Clear() => _written = 0;

    /// <summary>Copies the bytes written to a new array.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>
    /// Returns the array to the pool. You can call this method two times.
    /// </summary>
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;

        if (buffer.Length > 0)
        {
            // Cleared, because the array still holds the bytes of the caller.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "The size hint cannot be negative.");

        // The contract of IBufferWriter is that a hint of 0 asks for a buffer that is not empty.
        if (sizeHint == 0)
            sizeHint = 1;

        if (_buffer.Length - _written >= sizeHint)
            return;

        if (_buffer.Length == 0)
            throw new ObjectDisposedException(nameof(BsonBufferWriter));

        Grow(sizeHint);
    }

    private void Grow(int sizeHint)
    {
        var required = (long)_written + sizeHint;

        if (required > int.MaxValue)
        {
            throw new OutOfMemoryException(
                $"The writer needs {required} bytes, which one array cannot hold.");
        }

        // Doubling, or the exact requirement when one value is larger than that.
        var capacity = (int)Math.Max(required, Math.Min((long)_buffer.Length * 2, int.MaxValue));

        var larger = ArrayPool<byte>.Shared.Rent(capacity);
        Array.Copy(_buffer, larger, _written);

        var old = _buffer;
        _buffer = larger;
        ArrayPool<byte>.Shared.Return(old, clearArray: true);
    }
}
