// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Diagnostics;

namespace MiniBson.Polyfills;

#if !NETSTANDARD2_1_OR_GREATER
/// <summary>
/// Represents a heap-based, array-backed output sink into which <typeparam name="T"/> data can be written
/// </summary>
/// <remarks>
/// A polyfill of https://github.com/dotnet/runtime/blob/v10.0.10/src/libraries/Common/src/System/Buffers/ArrayBufferWriter.cs
/// </remarks>
public sealed class ArrayBufferWriter<T> : IBufferWriter<T>
{
    private const int ArrayMaxLength = 0x7FFFFFC7;

    private const int DefaultInitialBufferSize = 256;

    private T[] _buffer;
    private int _index;

    public ArrayBufferWriter()
    {
        _buffer = [];
        _index = 0;
    }

    public ArrayBufferWriter(int initialCapacity)
    {
        if (initialCapacity <= 0)
            throw new ArgumentException(null, nameof(initialCapacity));

        _buffer = new T[initialCapacity];
        _index = 0;
    }

    public ReadOnlyMemory<T> WrittenMemory => _buffer.AsMemory(0, _index);
    public ReadOnlySpan<T> WrittenSpan => _buffer.AsSpan(0, _index);
    public int WrittenCount => _index;
    public int Capacity => _buffer.Length;
    public int FreeCapacity => _buffer.Length - _index;

    public void Clear()
    {
        Debug.Assert(_buffer.Length >= _index);
        _buffer.AsSpan(0, _index).Clear();
        _index = 0;
    }

    public void ResetWrittenCount() => _index = 0;

    public void Advance(int count)
    {
        if (count < 0)
            throw new ArgumentException(null, nameof(count));

        if (_index > _buffer.Length - count)
            ThrowInvalidOperationException_AdvancedTooFar(_buffer.Length);

        _index += count;
    }

    public Memory<T> GetMemory(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        Debug.Assert(_buffer.Length > _index);
        return _buffer.AsMemory(_index);
    }

    public Span<T> GetSpan(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        Debug.Assert(_buffer.Length > _index);
        return _buffer.AsSpan(_index);
    }

    private void CheckAndResizeBuffer(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentException(nameof(sizeHint));

        if (sizeHint == 0)
        {
            sizeHint = 1;
        }

        if (sizeHint > FreeCapacity)
        {
            var currentLength = _buffer.Length;

            // Attempt to grow by the larger of the sizeHint and double the current size.
            var growBy = Math.Max(sizeHint, currentLength);

            if (currentLength == 0)
            {
                growBy = Math.Max(growBy, DefaultInitialBufferSize);
            }

            var newSize = currentLength + growBy;

            if ((uint)newSize > int.MaxValue)
            {
                // Attempt to grow to ArrayMaxLength.
                var needed = (uint)(currentLength - FreeCapacity + sizeHint);
                Debug.Assert(needed > currentLength);

                if (needed > ArrayMaxLength)
                {
                    ThrowOutOfMemoryException(needed);
                }

                newSize = ArrayMaxLength;
            }

            Array.Resize(ref _buffer, newSize);
        }

        Debug.Assert(FreeCapacity > 0 && FreeCapacity >= sizeHint);
    }

    private static void ThrowInvalidOperationException_AdvancedTooFar(int capacity) => throw new InvalidOperationException("BufferWriterAdvancedTooFar");

    private static void ThrowOutOfMemoryException(uint capacity) => throw new OutOfMemoryException("BufferMaximumSizeExceeded");
}
#endif
