using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiniBson;

#if MINIBSON_PUBLIC
/// <summary>
/// A low-level, forward-only BSON writer.
/// </summary>
public sealed class BsonWriter : IDisposable
#else
/// <summary>
/// A low-level, forward-only BSON writer.
/// </summary>
internal sealed class BsonWriter : IDisposable
#endif
{
    private const int BufferSize = 8192;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    // Asked once, in the constructor, because everything depending on seekability — the origin
    // below, whether lengths have to be supplied, whether a placeholder can be revisited — has
    // to agree, and a stream is free to change its answer between calls.
    private readonly bool _canSeek;

    // Offset this writer started at, so a stream positioned mid-file still resolves.
    private readonly long _origin;

    // A fixed-size window, not a per-document buffer: memory does not grow with document size.
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _staged;

    // Total bytes written, staged and flushed alike. Used instead of Stream.Position, which
    // a non-seekable stream cannot report.
    private long _position;

    private readonly Stack<DocumentFrame> _openDocuments = new();
    private int _arrayIndex;
    private readonly Stack<int> _arrayIndexStack = new();
    private bool _disposed;

    private struct DocumentFrame
    {
        public long StartPosition;

        /// <summary>Length supplied by the caller, or 0 when the length is to be patched in.</summary>
        public int ExpectedLength;
    }

    public BsonWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        _canSeek = stream.CanSeek;
        _origin = _canSeek ? stream.Position : 0;
    }

    /// <summary>
    /// Indicates whether documents must be opened with a known length. This is true when the
    /// destination cannot be seeked, because the length placeholder could never be revisited.
    /// </summary>
    public bool RequiresKnownLength => !_canSeek;

    /// <summary>
    /// Writes the start of a BSON document whose length is not yet known. The length is
    /// written as a placeholder and patched in by <see cref="WriteEndDocument"/>, which
    /// requires a seekable stream.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The stream cannot be seeked. Use <see cref="WriteStartDocument(int)"/> instead.
    /// </exception>
    public void WriteStartDocument() => WriteStartDocument(0);

    /// <summary>
    /// Writes the start of a BSON document of known length, so nothing has to be patched
    /// afterwards. This is the only form that works on a stream that cannot be seeked.
    /// </summary>
    /// <param name="documentLength">
    /// The complete encoded length of the document, including the four-byte length prefix and
    /// the trailing null terminator, as computed by <see cref="BsonSize"/>. Pass 0 when the
    /// length is unknown, which falls back to patching and requires a seekable stream.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="documentLength"/> is negative, or is too small to be a valid document.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The length is unknown and the stream cannot be seeked.
    /// </exception>
    public void WriteStartDocument(int documentLength)
    {
        ValidateDocumentLength(documentLength);

        _openDocuments.Push(new DocumentFrame
        {
            StartPosition = _position,
            ExpectedLength = documentLength,
        });

        // Either the real length, or a placeholder patched by WriteEndDocument.
        WriteInt32Raw(documentLength);
    }

    /// <summary>
    /// Rejects a document length this writer could not honour. Separate from
    /// <see cref="WriteStartDocument(int)"/> so callers that emit an element header first can
    /// fail before writing anything.
    /// </summary>
    private void ValidateDocumentLength(int documentLength)
    {
        if (documentLength != 0 && documentLength < BsonSize.DocumentOverhead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentLength),
                documentLength,
                $"A BSON document is at least {BsonSize.DocumentOverhead} bytes. Pass 0 if the length is unknown.");
        }

        if (documentLength == 0 && RequiresKnownLength)
        {
            throw new InvalidOperationException(
                "This stream cannot be seeked, so a document length placeholder could never be filled in. " +
                "Pass the document length to WriteStartDocument, or use a seekable stream.");
        }
    }

    /// <summary>
    /// Writes the end of a BSON document. Closing the outermost document also drains the
    /// staging buffer, so a complete document is always on the destination.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The document was opened with a length that does not match the bytes actually written,
    /// or it grew past what a BSON length prefix can express.
    /// </exception>
    public void WriteEndDocument()
    {
        WriteByteRaw(0); // End of document marker

        var frame = _openDocuments.Pop();
        var length64 = _position - frame.StartPosition;

        // The prefix is an int32, so a longer document could not describe itself. Checked
        // here rather than at the cast, which would wrap into a plausible-looking length.
        if (length64 > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Document is {length64} bytes, which a BSON length prefix cannot express " +
                $"(the maximum is {int.MaxValue}).");
        }

        var length = (int)length64;

        if (frame.ExpectedLength == 0)
        {
            PatchLength(frame.StartPosition, length);
        }
        // Not conditional on build configuration: a wrong length would otherwise produce a
        // silently malformed document.
        else if (frame.ExpectedLength != length)
        {
            throw new InvalidOperationException(
                $"Document was opened with a length of {frame.ExpectedLength} bytes but {length} bytes were written. " +
                "The computed size does not match the serialized value.");
        }

        // A closed top-level document is complete, so staging it no longer buys anything and
        // holding it back would surprise a caller reading the destination.
        if (_openDocuments.Count == 0)
            Drain();
    }

    /// <summary>
    /// Fills in the length placeholder written by <see cref="WriteStartDocument()"/>.
    /// </summary>
    private void PatchLength(long startPosition, int length)
    {
        // Offset of _buffer[0] within the logical byte sequence.
        var bufferOrigin = _position - _staged;

        if (startPosition >= bufferOrigin)
        {
            // Still staged. Stage() never splits a scalar, so all four bytes are contiguous.
            BinaryPrimitives.WriteInt32LittleEndian(
                _buffer.AsSpan((int)(startPosition - bufferOrigin), 4), length);
            return;
        }

        // The placeholder is already on the stream; reaching it requires seeking.
        Drain();
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(0, 4), length);
        _stream.Position = _origin + startPosition;
        _stream.Write(_buffer, 0, 4);
        _stream.Position = _origin + _position;
    }

    /// <summary>
    /// Writes the start of a BSON array.
    /// </summary>
    /// <param name="name">Element name.</param>
    /// <param name="documentLength">
    /// The complete encoded length of the array document, or 0 when it is unknown. See
    /// <see cref="WriteStartDocument(int)"/> and <see cref="BsonSize.ArrayOverhead"/>.
    /// </param>
    public void WriteStartArray(string name, int documentLength = 0)
    {
        // Before the element header, so a rejected length leaves nothing behind to orphan.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Array);
        WriteCString(name);
        _arrayIndexStack.Push(_arrayIndex);
        _arrayIndex = 0;
        WriteStartDocument(documentLength);
    }

    /// <summary>
    /// Writes the end of a BSON array.
    /// </summary>
    public void WriteEndArray()
    {
        // Restored even when the length check rejects the document, so a caller that handles
        // that exception does not go on numbering the enclosing array from the wrong base.
        try
        {
            WriteEndDocument();
        }
        finally
        {
            _arrayIndex = _arrayIndexStack.Pop();
        }
    }

    /// <summary>
    /// Writes a nested document field.
    /// </summary>
    /// <param name="name">Element name.</param>
    /// <param name="documentLength">
    /// The complete encoded length of the nested document, or 0 when it is unknown.
    /// See <see cref="WriteStartDocument(int)"/>.
    /// </param>
    public void WriteStartDocument(string name, int documentLength = 0)
    {
        // Before the element header, so a rejected length leaves nothing behind to orphan.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Document);
        WriteCString(name);
        WriteStartDocument(documentLength);
    }

    /// <summary>
    /// Writes a null value.
    /// </summary>
    public void WriteNull(string name)
    {
        WriteType(BsonType.Null);
        WriteCString(name);
    }

    /// <summary>
    /// Writes a boolean value.
    /// </summary>
    public void WriteBoolean(string name, bool value)
    {
        WriteType(BsonType.Boolean);
        WriteCString(name);
        WriteByteRaw(value ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Writes a 32-bit integer value.
    /// </summary>
    public void WriteInt32(string name, int value)
    {
        WriteType(BsonType.Int32);
        WriteCString(name);
        WriteInt32Raw(value);
    }

    /// <summary>
    /// Writes a 64-bit integer value.
    /// </summary>
    public void WriteInt64(string name, long value)
    {
        WriteType(BsonType.Int64);
        WriteCString(name);
        WriteInt64Raw(value);
    }

    /// <summary>
    /// Writes a double-precision floating point value.
    /// </summary>
    public void WriteDouble(string name, double value)
    {
        WriteType(BsonType.Double);
        WriteCString(name);
        WriteDoubleRaw(value);
    }

    /// <summary>
    /// Writes a string value.
    /// </summary>
    public void WriteString(string name, string value)
    {
        WriteType(BsonType.String);
        WriteCString(name);
        WriteStringValue(value);
    }

    /// <summary>
    /// Writes a DateTime value as BSON DateTime (UTC milliseconds since Unix epoch).
    /// </summary>
    public void WriteDateTime(string name, DateTime value)
    {
        WriteType(BsonType.DateTime);
        WriteCString(name);
        WriteDateTimeValue(value);
    }

    private void WriteDateTimeValue(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        var milliseconds = (long)(utc - UnixEpoch).TotalMilliseconds;
        WriteInt64Raw(milliseconds);
    }

    /// <summary>
    /// Writes a BSON ObjectId (12 bytes).
    /// </summary>
    public void WriteObjectId(string name, ReadOnlySpan<byte> value)
    {
        if (value.Length != 12)
            throw new ArgumentException("ObjectId must be exactly 12 bytes.", nameof(value));
        
        WriteType(BsonType.ObjectId);
        WriteCString(name);
        WriteBytesRaw(value);
    }

    /// <summary>
    /// Writes binary data.
    /// </summary>
    public void WriteBinary(string name, ReadOnlySpan<byte> value, BsonBinarySubType subType = BsonBinarySubType.Generic)
    {
        WriteType(BsonType.Binary);
        WriteCString(name);
        WriteBinaryValue(value, subType);
    }

    private void WriteBinaryValue(ReadOnlySpan<byte> value, BsonBinarySubType subType)
    {
        if (subType == BsonBinarySubType.BinaryOld)
        {
            // Old binary format includes an extra length prefix
            WriteInt32Raw(value.Length + 4);
            WriteByteRaw((byte)subType);
            WriteInt32Raw(value.Length);
        }
        else
        {
            WriteInt32Raw(value.Length);
            WriteByteRaw((byte)subType);
        }

        WriteBytesRaw(value);
    }

    /// <summary>
    /// Writes a GUID as binary with UUID subtype.
    /// </summary>
    public void WriteGuid(string name, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
#if NET6_0_OR_GREATER
        value.TryWriteBytes(bytes);
#else
        value.ToByteArray().CopyTo(bytes);
#endif
        WriteBinary(name, bytes, BsonBinarySubType.Uuid);
    }

    /// <summary>
    /// Writes a regular expression.
    /// </summary>
    public void WriteRegex(string name, string pattern, string options = "")
    {
        WriteType(BsonType.Regex);
        WriteCString(name);
        WriteCString(pattern);
        WriteCString(options);
    }

    /// <summary>
    /// Writes a JavaScript code string.
    /// </summary>
    public void WriteJavaScript(string name, string code)
    {
        WriteType(BsonType.JavaScript);
        WriteCString(name);
        WriteStringValue(code);
    }

    /// <summary>
    /// Writes a BSON timestamp (used internally by MongoDB).
    /// </summary>
    public void WriteTimestamp(string name, uint increment, uint timestamp)
    {
        WriteType(BsonType.Timestamp);
        WriteCString(name);
        WriteUInt32Raw(increment);
        WriteUInt32Raw(timestamp);
    }

    // Array element writers. BSON names array elements with their decimal index, so each
    // writes the same value as its named counterpart behind a positional header.

    /// <summary>
    /// Writes the next element's name straight to the buffer as ASCII digits, so element
    /// names cost no allocation.
    /// </summary>
    private void WriteNextArrayKey()
    {
        var index = _arrayIndex++;

        // int.MaxValue is 10 digits, filled from the least significant end.
        Span<byte> digits = stackalloc byte[10];
        var start = digits.Length;
        do
        {
            digits[--start] = (byte)('0' + index % 10);
            index /= 10;
        }
        while (index > 0);

        WriteBytesRaw(digits.Slice(start));
        WriteByteRaw(0);
    }

    /// <summary>
    /// Writes a null array element.
    /// </summary>
    public void WriteNull()
    {
        WriteType(BsonType.Null);
        WriteNextArrayKey();
    }

    /// <summary>
    /// Writes a boolean array element.
    /// </summary>
    public void WriteBoolean(bool value)
    {
        WriteType(BsonType.Boolean);
        WriteNextArrayKey();
        WriteByteRaw(value ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Writes an int32 array element.
    /// </summary>
    public void WriteInt32(int value)
    {
        WriteType(BsonType.Int32);
        WriteNextArrayKey();
        WriteInt32Raw(value);
    }

    /// <summary>
    /// Writes an int64 array element.
    /// </summary>
    public void WriteInt64(long value)
    {
        WriteType(BsonType.Int64);
        WriteNextArrayKey();
        WriteInt64Raw(value);
    }

    /// <summary>
    /// Writes a double array element.
    /// </summary>
    public void WriteDouble(double value)
    {
        WriteType(BsonType.Double);
        WriteNextArrayKey();
        WriteDoubleRaw(value);
    }

    /// <summary>
    /// Writes a string array element.
    /// </summary>
    public void WriteString(string value)
    {
        WriteType(BsonType.String);
        WriteNextArrayKey();
        WriteStringValue(value);
    }

    /// <summary>
    /// Writes a DateTime array element.
    /// </summary>
    public void WriteDateTime(DateTime value)
    {
        WriteType(BsonType.DateTime);
        WriteNextArrayKey();
        WriteDateTimeValue(value);
    }

    /// <summary>
    /// Writes a binary array element.
    /// </summary>
    public void WriteBinary(ReadOnlySpan<byte> value, BsonBinarySubType subType = BsonBinarySubType.Generic)
    {
        WriteType(BsonType.Binary);
        WriteNextArrayKey();
        WriteBinaryValue(value, subType);
    }

    /// <summary>
    /// Writes a GUID array element.
    /// </summary>
    public void WriteGuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
#if NET6_0_OR_GREATER
        value.TryWriteBytes(bytes);
#else
        value.ToByteArray().CopyTo(bytes);
#endif
        WriteBinary(bytes, BsonBinarySubType.Uuid);
    }

    /// <summary>
    /// Writes a nested document array element.
    /// </summary>
    /// <param name="documentLength">
    /// The complete encoded length of the nested document, or 0 when it is unknown.
    /// See <see cref="WriteStartDocument(int)"/>.
    /// </param>
    public void WriteStartNestedDocument(int documentLength = 0)
    {
        // Before the element header, so a rejected length does not consume an array index.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Document);
        WriteNextArrayKey();
        WriteStartDocument(documentLength);
    }

    /// <summary>
    /// Writes a nested array element.
    /// </summary>
    /// <param name="documentLength">
    /// The complete encoded length of the nested array, or 0 when it is unknown.
    /// See <see cref="WriteStartDocument(int)"/>.
    /// </param>
    public void WriteStartNestedArray(int documentLength = 0)
    {
        // Before the element header, so a rejected length does not consume an array index.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Array);
        // Consumes this element's index before the nested array resets the counter.
        WriteNextArrayKey();
        _arrayIndexStack.Push(_arrayIndex);
        _arrayIndex = 0;
        WriteStartDocument(documentLength);
    }

    private void WriteType(BsonType type)
    {
        WriteByteRaw((byte)type);
    }

    private void WriteCString(string value)
    {
#if NET6_0_OR_GREATER
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = byteCount <= 256
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        var written = Encoding.UTF8.GetBytes(value, buffer);
        WriteBytesRaw(buffer.Slice(0, written));
#else
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBytesRaw(bytes);
#endif
        WriteByteRaw(0);
    }

    private void WriteStringValue(string value)
    {
#if NET6_0_OR_GREATER
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32Raw(byteCount + 1); // length includes null terminator
        Span<byte> buffer = byteCount <= 256
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        var written = Encoding.UTF8.GetBytes(value, buffer);
        WriteBytesRaw(buffer.Slice(0, written));
#else
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32Raw(bytes.Length + 1); // length includes null terminator
        WriteBytesRaw(bytes);
#endif
        WriteByteRaw(0);
    }

    // Staging primitives. Every byte this writer emits passes through one of these and
    // leaves through the single Flush() below, which is what keeps an alternative destination
    // (IBufferWriter, PipeWriter) a local change rather than a rewrite.

    /// <summary>
    /// Reserves <paramref name="count"/> contiguous staged bytes. Scalars only; bulk payloads
    /// go through <see cref="WriteBytesRaw"/>.
    /// </summary>
    private Span<byte> Stage(int count)
    {
        ThrowIfDisposed();

        if (_staged + count > _buffer.Length)
            Drain();

        var span = _buffer.AsSpan(_staged, count);
        _staged += count;
        _position += count;
        return span;
    }

    private void WriteByteRaw(byte value)
    {
        ThrowIfDisposed();

        if (_staged == _buffer.Length)
            Drain();

        _buffer[_staged++] = value;
        _position++;
    }

    private void WriteInt32Raw(int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(Stage(4), value);

    private void WriteUInt32Raw(uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Stage(4), value);

    private void WriteInt64Raw(long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(Stage(8), value);

    // Raw bits keep this little-endian on either endianness, matching BinaryWriter.
    private void WriteDoubleRaw(double value) =>
        WriteInt64Raw(BitConverter.DoubleToInt64Bits(value));

    private void WriteBytesRaw(ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();

        if (value.Length <= _buffer.Length - _staged)
        {
            value.CopyTo(_buffer.AsSpan(_staged));
            _staged += value.Length;
            _position += value.Length;
            return;
        }

        Drain();

        if (value.Length <= _buffer.Length)
        {
            value.CopyTo(_buffer.AsSpan(0));
            _staged = value.Length;
            _position += value.Length;
            return;
        }

        // Larger than the staging window: go straight to the stream rather than growing the
        // buffer, so writer memory stays bounded.
        _position += value.Length;
#if NET6_0_OR_GREATER
        _stream.Write(value);
#else
        while (!value.IsEmpty)
        {
            var chunk = Math.Min(value.Length, _buffer.Length);
            value.Slice(0, chunk).CopyTo(_buffer.AsSpan(0));
            _stream.Write(_buffer, 0, chunk);
            value = value.Slice(chunk);
        }
#endif
    }

    /// <summary>
    /// Writes any staged bytes to the underlying stream and flushes the stream itself, so the
    /// bytes reach the ultimate destination behind any wrapper holding its own buffer.
    /// </summary>
    /// <remarks>
    /// Only needed to publish a partially written document: closing a top-level document
    /// already drains the staging buffer, and <see cref="Dispose"/> flushes.
    /// </remarks>
    public void Flush()
    {
        ThrowIfDisposed();
        Drain();
        _stream.Flush();
    }

    /// <summary>
    /// Moves staged bytes to the stream without flushing it. This is the internal half of
    /// <see cref="Flush"/>: it runs whenever the staging buffer fills, where flushing the
    /// destination on every window would defeat any buffering the caller wrapped it in.
    /// </summary>
    private void Drain()
    {
        if (_staged == 0)
            return;

        _stream.Write(_buffer, 0, _staged);
        _staged = 0;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BsonWriter));
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            Drain();

            // Disposing the stream flushes it, so this only matters for one the caller keeps.
            if (_leaveOpen)
                _stream.Flush();
        }
        finally
        {
            var buffer = _buffer;
            _buffer = [];
            // Cleared: the pool hands this array to the next renter as-is, and it still holds
            // whatever was serialized through it.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);

            if (!_leaveOpen)
                _stream.Dispose();
        }
    }
}


