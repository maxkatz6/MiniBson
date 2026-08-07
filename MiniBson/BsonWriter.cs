using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiniBson;

/// <summary>
/// A low-level, forward-only BSON writer.
/// </summary>
#if MINIBSON_PUBLIC
public sealed class BsonWriter : IDisposable
#else
internal sealed class BsonWriter : IDisposable
#endif
{
    private const int BufferSize = 8192;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    // The constructor asks the stream one time. Three things use this answer: the origin below,
    // the rule for supplied lengths, and the method that writes a placeholder later. They must
    // agree, and a stream can give a different answer to each call.
    private readonly bool _canSeek;

    // The offset where this writer started. Thus a stream that starts in the middle of a file
    // still gives correct positions.
    private readonly long _origin;

    // A buffer of a fixed length, not one buffer for each document. Thus the memory does not
    // increase with the document length.
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _staged;

    // A count of all bytes that the writer wrote, in the buffer and on the stream. The writer
    // uses this count instead of Stream.Position, which a stream that cannot seek does not give.
    private long _position;

    private readonly Stack<DocumentFrame> _openDocuments = new();
    private int _arrayIndex;
    private readonly Stack<int> _arrayIndexStack = new();
    private bool _disposed;

    private struct DocumentFrame
    {
        public long StartPosition;

        /// <summary>The length from the caller, or 0 when the writer writes the length later.</summary>
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
    /// True when you must start each document with a known length. This is true when the
    /// destination cannot seek, because the writer can never go back to a placeholder.
    /// </summary>
    public bool RequiresKnownLength => !_canSeek;

    /// <summary>
    /// Writes the start of a BSON document with an unknown length. The writer writes a
    /// placeholder, and <see cref="WriteEndDocument"/> writes the correct length there. This
    /// method needs a stream that can seek.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The stream cannot seek. Use <see cref="WriteStartDocument(int)"/> in its place.
    /// </exception>
    public void WriteStartDocument() => WriteStartDocument(0);

    /// <summary>
    /// Writes the start of a BSON document with a known length. Thus the writer writes no
    /// length later. This is the only form that works with a stream that cannot seek.
    /// </summary>
    /// <param name="documentLength">
    /// The full encoded length of the document. It includes the four-byte length prefix and
    /// the null terminator at the end. <see cref="BsonSize"/> computes it. Give 0 when the
    /// length is unknown. The writer then writes the length later and needs a stream that can
    /// seek.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="documentLength"/> is negative, or it is too small for a valid document.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The length is unknown and the stream cannot seek.
    /// </exception>
    public void WriteStartDocument(int documentLength)
    {
        ValidateDocumentLength(documentLength);
        BeginDocument(documentLength);
    }

    /// <summary>
    /// Opens a document with a length that the writer already tested. The named forms and the
    /// positional forms below test the length before they write the element header. Thus a
    /// length that fails leaves no bytes on the destination. A call to
    /// <see cref="WriteStartDocument(int)"/> would test the length a second time.
    /// </summary>
    private void BeginDocument(int documentLength)
    {
        _openDocuments.Push(new DocumentFrame
        {
            StartPosition = _position,
            ExpectedLength = documentLength,
        });

        // This is the correct length, or a placeholder that WriteEndDocument fills in.
        WriteInt32Raw(documentLength);
    }

    /// <summary>
    /// Rejects a document length that this writer cannot accept. It is separate from
    /// <see cref="WriteStartDocument(int)"/>, so a caller that writes an element header first
    /// can fail before it writes any byte.
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
    /// Writes the end of a BSON document. On the outermost document, this method also drains
    /// the buffer. Thus a complete document is always on the destination.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The length at the start of the document does not agree with the bytes that the writer
    /// wrote, or the document is longer than a BSON length prefix can express.
    /// </exception>
    public void WriteEndDocument()
    {
        WriteByteRaw(0); // End of document marker

        var frame = _openDocuments.Pop();
        var length64 = _position - frame.StartPosition;

        // The prefix is an int32, so a longer document cannot give its own length. The test is
        // here and not at the cast, because the cast would wrap and give an incorrect length.
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
        // This test runs in each build configuration. Without it, a wrong length gives a bad
        // document and no error.
        else if (frame.ExpectedLength != length)
        {
            throw new InvalidOperationException(
                $"Document was opened with a length of {frame.ExpectedLength} bytes but {length} bytes were written. " +
                "The computed size does not match the serialized value.");
        }

        // A closed top-level document is complete. The buffer gives no advantage now, and a
        // caller that reads the destination does not expect an incomplete document.
        if (_openDocuments.Count == 0)
            Drain();
    }

    /// <summary>
    /// Fills in the length placeholder from <see cref="WriteStartDocument()"/>.
    /// </summary>
    private void PatchLength(long startPosition, int length)
    {
        // The offset of _buffer[0] in the full byte sequence.
        var bufferOrigin = _position - _staged;

        if (startPosition >= bufferOrigin)
        {
            // The placeholder is still in the buffer. Stage() never divides a scalar, so the
            // four bytes are adjacent.
            BinaryPrimitives.WriteInt32LittleEndian(
                _buffer.AsSpan((int)(startPosition - bufferOrigin), 4), length);
            return;
        }

        // The placeholder is already on the stream, so the writer must do a seek to reach it.
        Drain();
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(0, 4), length);
        _stream.Position = _origin + startPosition;
        _stream.Write(_buffer, 0, 4);
        _stream.Position = _origin + _position;
    }

    /// <summary>
    /// Writes the start of a BSON array.
    /// </summary>
    /// <param name="name">The element name.</param>
    /// <param name="documentLength">
    /// The full encoded length of the array document, or 0 when it is unknown. See
    /// <see cref="WriteStartDocument(int)"/> and <see cref="BsonSize.ArrayOverhead"/>.
    /// </param>
    public void WriteStartArray(string name, int documentLength = 0)
    {
        // This test is before the element header. Thus a length that fails leaves no bytes.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Array);
        WriteCString(name);
        PushArrayScope();
        BeginDocument(documentLength);
    }

    /// <summary>
    /// Starts the element numbers of a new array at zero. It keeps the counter of the outer
    /// array, and <see cref="WriteEndArray"/> puts that counter back.
    /// </summary>
    private void PushArrayScope()
    {
        _arrayIndexStack.Push(_arrayIndex);
        _arrayIndex = 0;
    }

    /// <summary>
    /// Writes the end of a BSON array.
    /// </summary>
    public void WriteEndArray()
    {
        // The writer puts the counter back even when the length test rejects the document. Thus
        // a caller that catches that exception continues the outer array at the correct number.
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
    /// Writes a nested document element.
    /// </summary>
    /// <param name="name">The element name.</param>
    /// <param name="documentLength">
    /// The full encoded length of the nested document, or 0 when it is unknown.
    /// See <see cref="WriteStartDocument(int)"/>.
    /// </param>
    public void WriteStartDocument(string name, int documentLength = 0)
    {
        // This test is before the element header. Thus a length that fails leaves no bytes.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Document);
        WriteCString(name);
        BeginDocument(documentLength);
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
    /// Writes a DateTime value as a BSON DateTime, in UTC milliseconds after the Unix epoch.
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
            // The old binary format has one more length prefix.
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
    /// Writes a GUID as binary data with the UUID subtype.
    /// </summary>
    public void WriteGuid(string name, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        WriteGuidBytes(value, bytes);
        WriteBinary(name, bytes, BsonBinarySubType.Uuid);
    }

    private static void WriteGuidBytes(Guid value, Span<byte> destination)
    {
#if NET6_0_OR_GREATER
        value.TryWriteBytes(destination);
#else
        value.ToByteArray().CopyTo(destination);
#endif
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
    /// Writes a BSON timestamp. MongoDB uses this type internally.
    /// </summary>
    public void WriteTimestamp(string name, uint increment, uint timestamp)
    {
        WriteType(BsonType.Timestamp);
        WriteCString(name);
        WriteUInt32Raw(increment);
        WriteUInt32Raw(timestamp);
    }

    // The array element writers. BSON gives each array element a decimal index as its name.
    // Thus each method writes the same value as its named equivalent, but with an index header.

    /// <summary>
    /// Writes the name of the next element directly to the buffer as ASCII digits. Thus an
    /// element name allocates no memory.
    /// </summary>
    private void WriteNextArrayKey()
    {
        var index = _arrayIndex++;

        // int.MaxValue has 10 digits. The code fills them from the last digit.
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
        WriteGuidBytes(value, bytes);
        WriteBinary(bytes, BsonBinarySubType.Uuid);
    }

    /// <summary>
    /// Writes a nested document array element.
    /// </summary>
    /// <param name="documentLength">
    /// The full encoded length of the nested document, or 0 when it is unknown.
    /// See <see cref="WriteStartDocument(int)"/>.
    /// </param>
    public void WriteStartNestedDocument(int documentLength = 0)
    {
        // This test is before the element header. Thus a length that fails uses no array index.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Document);
        WriteNextArrayKey();
        BeginDocument(documentLength);
    }

    /// <summary>
    /// Writes a nested array element.
    /// </summary>
    /// <param name="documentLength">
    /// The full encoded length of the nested array, or 0 when it is unknown.
    /// See <see cref="WriteStartDocument(int)"/>.
    /// </param>
    public void WriteStartNestedArray(int documentLength = 0)
    {
        // This test is before the element header. Thus a length that fails uses no array index.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Array);
        // This uses the index of the element before the nested array sets the counter to zero.
        WriteNextArrayKey();
        PushArrayScope();
        BeginDocument(documentLength);
    }

    private void WriteType(BsonType type)
    {
        WriteByteRaw((byte)type);
    }

    /// <summary>
    /// A string with a null terminator and no length prefix. An element name and each part of
    /// a regular expression use this form.
    /// </summary>
    private void WriteCString(string value) => WriteUtf8(value, lengthPrefixed: false);

    /// <summary>
    /// A string with a length prefix and a null terminator. A String value and a JavaScript
    /// value use this form.
    /// </summary>
    private void WriteStringValue(string value) => WriteUtf8(value, lengthPrefixed: true);

    /// <summary>
    /// Encodes <paramref name="value"/> as UTF-8 and writes it with its terminator. It also
    /// writes a length in front of the value when BSON needs one.
    /// </summary>
    private void WriteUtf8(string value, bool lengthPrefixed)
    {
#if NET6_0_OR_GREATER
        var byteCount = Encoding.UTF8.GetByteCount(value);

        if (lengthPrefixed)
            WriteInt32Raw(byteCount + 1); // the declared length counts the terminator

        // Most values are names or short strings. Only a very large value goes to the heap.
        Span<byte> buffer = byteCount <= 256
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        var written = Encoding.UTF8.GetBytes(value, buffer);
        WriteBytesRaw(buffer.Slice(0, written));
#else
        var bytes = Encoding.UTF8.GetBytes(value);

        if (lengthPrefixed)
            WriteInt32Raw(bytes.Length + 1); // the declared length counts the terminator

        WriteBytesRaw(bytes);
#endif
        WriteByteRaw(0);
    }

    // The buffer primitives. Each byte that this writer produces goes through one of these
    // methods and then through the one Drain() below. Thus a different destination
    // (IBufferWriter, PipeWriter) is a local change and not a new writer.

    /// <summary>
    /// Keeps <paramref name="count"/> adjacent bytes in the buffer. Use this method only for a
    /// scalar value. A large value goes through <see cref="WriteBytesRaw"/>.
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

    // The raw bits keep this value little-endian on all machines, the same as BinaryWriter.
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

        // The value is longer than the buffer. Write it directly to the stream and do not make
        // the buffer larger. Thus the writer memory has a limit.
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
    /// Writes the bytes in the buffer to the stream and then flushes the stream. Thus the bytes
    /// reach the final destination behind each wrapper that has its own buffer.
    /// </summary>
    /// <remarks>
    /// You need this method only for an incomplete document. <see cref="WriteEndDocument"/> on
    /// the top-level document already drains the buffer, and <see cref="Dispose"/> flushes.
    /// </remarks>
    public void Flush()
    {
        ThrowIfDisposed();
        Drain();
        _stream.Flush();
    }

    /// <summary>
    /// Moves the bytes in the buffer to the stream and does not flush it. This is the internal
    /// part of <see cref="Flush"/>. It runs each time the buffer becomes full. A flush of the
    /// destination each time would prevent the caller's own buffer from doing its work.
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

            // Dispose on the stream flushes it. Thus this call is necessary only for a stream
            // that the caller keeps open.
            if (_leaveOpen)
                _stream.Flush();
        }
        finally
        {
            var buffer = _buffer;
            _buffer = [];
            // Clear the array. The pool gives it to the next caller without a change, and it
            // still holds the data that the writer wrote through it.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);

            if (!_leaveOpen)
                _stream.Dispose();
        }
    }
}
