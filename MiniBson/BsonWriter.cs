using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MiniBson;

/// <summary>
/// A low-level, forward-only BSON writer over an <see cref="IBufferWriter{T}"/>.
/// </summary>
#if MINIBSON_PUBLIC
public sealed class BsonWriter(IBufferWriter<byte> output)
#else
internal sealed class BsonWriter(IBufferWriter<byte> output)
#endif
{
    // The largest buffer that the writer asks for at one time. A longer document still works. It
    // uses more than one buffer. This limit stops one large document from asking a PipeWriter for
    // one very large segment.
    private const int MaxSizeHint = 64 * 1024;

    private readonly IBufferWriter<byte> _output = output ?? throw new ArgumentNullException(nameof(output));

    // The buffer from the destination. It stays valid until the next GetMemory or Advance call on
    // the destination. A write into it does not make it invalid.
    private Memory<byte> _memory;

    // The bytes in _memory that Advance has not committed.
    private int _buffered;

    // A count of all bytes that the writer produced, in the buffer and committed. The writer
    // keeps this count itself, because the destination reports no position.
    private long _position;

    private DocumentFrame[] _frames = new DocumentFrame[4];
    private int _depth;
    private int _arrayIndex;

    private struct DocumentFrame
    {
        public long StartPosition;

        /// <summary>The length the caller gave. <see cref="WriteEndDocument"/> checks it.</summary>
        public int ExpectedLength;

        /// <summary>
        /// The element counter of the array outside this document. It is part of the frame. Thus
        /// the end of a document and the end of an array are the same operation.
        /// </summary>
        public int SavedArrayIndex;
    }

    /// <summary>
    /// Writes the start of a BSON document.
    /// </summary>
    /// <param name="documentLength">
    /// The full encoded length of the document. It includes the four-byte length prefix and the
    /// null terminator at the end. <see cref="BsonSize"/> computes it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="documentLength"/> is too small for a valid document.
    /// </exception>
    public void WriteStartDocument(int documentLength)
    {
        ValidateDocumentLength(documentLength);
        BeginDocument(documentLength, isArray: false);
    }

    /// <summary>
    /// Rejects a document length that this writer cannot accept. It is separate from
    /// <see cref="WriteStartDocument(int)"/>, so a caller that writes an element header first can
    /// fail before it writes any byte and before it uses an array index.
    /// </summary>
    private static void ValidateDocumentLength(int documentLength)
    {
        if (documentLength < BsonSize.DocumentOverhead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(documentLength),
                documentLength,
                $"A BSON document is at least {BsonSize.DocumentOverhead} bytes. Compute the length with BsonSize.");
        }
    }

    /// <summary>
    /// Opens a document with a length that the caller already tested, and writes that length.
    /// </summary>
    private void BeginDocument(int documentLength, bool isArray)
    {
        if (_depth == _frames.Length)
            Array.Resize(ref _frames, _frames.Length * 2);

        var savedArrayIndex = _arrayIndex;

        _frames[_depth++] = new DocumentFrame
        {
            StartPosition = _position,
            ExpectedLength = documentLength,
            SavedArrayIndex = savedArrayIndex,
        };

        // An array numbers its elements from zero. WriteEndDocument puts back the counter of the
        // array outside this one.
        if (isArray)
            _arrayIndex = 0;

        try
        {
            WriteInt32Raw(documentLength);
        }
        catch
        {
            _depth--;
            _arrayIndex = savedArrayIndex;
            throw;
        }
    }

    /// <summary>
    /// Writes the end of a BSON document or array. On the outermost document, this method also
    /// commits the bytes to the destination. Thus a complete document is always there.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// There is no open document, or the length at the start of the document does not agree with
    /// the bytes that the writer wrote.
    /// </exception>
    public void WriteEndDocument()
    {
        if (_depth == 0)
            throw new InvalidOperationException("There is no open document to end.");

        WriteByteRaw(0); // End of document marker

        var frame = _frames[--_depth];

        // The counter goes back before the length test below, which can throw. Thus a caller that
        // catches that exception continues the outer array at the correct number.
        _arrayIndex = frame.SavedArrayIndex;

        // This test runs in each build configuration. It is the one test that finds a length that
        // does not agree with the bytes. Without it, a wrong length gives a bad document and no
        // error. A document longer than int.MaxValue also fails here, because the expected length
        // cannot hold such a number.
        var length = _position - frame.StartPosition;
        if (length != frame.ExpectedLength)
        {
            throw new InvalidOperationException(
                $"Document was opened with a length of {frame.ExpectedLength} bytes but {length} bytes were written. " +
                "The computed size does not match the serialized value.");
        }

        // A closed top-level document is complete. The buffer gives no advantage now, and a
        // caller that reads the destination does not expect an incomplete document.
        if (_depth == 0)
            Flush();
    }

    /// <summary>
    /// Writes the start of a BSON array.
    /// </summary>
    /// <param name="name">The element name.</param>
    /// <param name="documentLength">
    /// The full encoded length of the array document. See <see cref="BsonSize.ArrayOverhead"/>.
    /// </param>
    public void WriteStartArray(string name, int documentLength)
    {
        // This test is before the element header. Thus a length that fails leaves no bytes.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Array);
        WriteCString(name);
        BeginDocument(documentLength, isArray: true);
    }

    /// <summary>
    /// Writes the end of a BSON array. It is the same operation as
    /// <see cref="WriteEndDocument"/>, because the element counter of the enclosing array rides
    /// in the frame of the document.
    /// </summary>
    public void WriteEndArray() => WriteEndDocument();

    /// <summary>
    /// Writes a nested document element.
    /// </summary>
    /// <param name="name">The element name.</param>
    /// <param name="documentLength">The full encoded length of the nested document.</param>
    public void WriteStartDocument(string name, int documentLength)
    {
        // This test is before the element header. Thus a length that fails leaves no bytes.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Document);
        WriteCString(name);
        BeginDocument(documentLength, isArray: false);
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
        if (value.Length != BsonSize.ObjectId)
            throw new ArgumentException($"ObjectId must be exactly {BsonSize.ObjectId} bytes.", nameof(value));

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
    /// <param name="documentLength">The full encoded length of the nested document.</param>
    public void WriteStartNestedDocument(int documentLength)
    {
        // This test is before the element header. Thus a length that fails uses no array index.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Document);
        WriteNextArrayKey();
        BeginDocument(documentLength, isArray: false);
    }

    /// <summary>
    /// Writes a nested array element.
    /// </summary>
    /// <param name="documentLength">The full encoded length of the nested array.</param>
    public void WriteStartNestedArray(int documentLength)
    {
        // This test is before the element header. Thus a length that fails uses no array index.
        ValidateDocumentLength(documentLength);

        WriteType(BsonType.Array);
        // This uses the index of the element before the nested array sets the counter to zero.
        WriteNextArrayKey();
        BeginDocument(documentLength, isArray: true);
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

        if (TryStageDirect(byteCount, out var destination))
        {
            // The destination has room. Thus the value goes there and needs no second buffer. A
            // name and a short string almost always take this path.
            Encoding.UTF8.GetBytes(value, destination);
        }
        else
        {
            // Only a very large value goes to the heap.
            Span<byte> buffer = byteCount <= 256
                ? stackalloc byte[byteCount]
                : new byte[byteCount];
            var written = Encoding.UTF8.GetBytes(value, buffer);
            WriteBytesRaw(buffer.Slice(0, written));
        }
#else
        var bytes = Encoding.UTF8.GetBytes(value);

        if (lengthPrefixed)
            WriteInt32Raw(bytes.Length + 1); // the declared length counts the terminator

        WriteBytesRaw(bytes);
#endif
        WriteByteRaw(0);
    }

    // The output primitives. Each byte that this writer produces goes through one of the four
    // methods below, and reaches the destination through Acquire and Flush.

    /// <summary>
    /// Keeps <paramref name="count"/> adjacent bytes in the buffer.
    /// </summary>
    /// <remarks>
    /// Call this method only with 12 bytes or fewer. That is a scalar value, or the digits of an
    /// array index. It is the one place that asks the destination for adjacent bytes, and a
    /// destination is allowed to give few bytes at a time. A longer value goes through
    /// <see cref="WriteBytesRaw"/>, which needs no adjacent bytes.
    /// </remarks>
    private Span<byte> Stage(int count)
    {
        if (_memory.Length - _buffered < count)
            Acquire(count);

        var span = _memory.Span.Slice(_buffered, count);
        _buffered += count;
        _position += count;
        return span;
    }

    private void WriteByteRaw(byte value)
    {
        if (_memory.Length - _buffered < 1)
            Acquire(1);

        _memory.Span[_buffered++] = value;
        _position++;
    }

    /// <summary>
    /// Returns the room for <paramref name="byteCount"/> adjacent bytes that the buffer already
    /// holds, and does not ask the destination for more. Returns false when it holds fewer.
    /// </summary>
    private bool TryStageDirect(int byteCount, out Span<byte> destination)
    {
        if (_memory.Length - _buffered < byteCount)
        {
            destination = default;
            return false;
        }

        destination = _memory.Span.Slice(_buffered, byteCount);
        _buffered += byteCount;
        _position += byteCount;
        return true;
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

    /// <summary>
    /// Writes any number of bytes. It fills the buffer, commits it, asks for another one, and
    /// repeats. Thus it needs no adjacent bytes and no buffer of its own, and it works with a
    /// destination that gives one byte at a time.
    /// </summary>
    private void WriteBytesRaw(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty)
        {
            if (_memory.Length - _buffered == 0)
                Acquire(1);

            var chunk = Math.Min(value.Length, _memory.Length - _buffered);
            value.Slice(0, chunk).CopyTo(_memory.Span.Slice(_buffered));
            _buffered += chunk;
            _position += chunk;
            value = value.Slice(chunk);
        }
    }

    /// <summary>
    /// Commits the current buffer and asks the destination for one that holds at least
    /// <paramref name="minimum"/> bytes.
    /// </summary>
    private void Acquire(int minimum)
    {
        Flush();
        _memory = _output.GetMemory(SizeHint(minimum));

        if (_memory.Length < minimum)
        {
            throw new InvalidOperationException(
                $"The IBufferWriter returned {_memory.Length} bytes for a size hint of {minimum}. " +
                "A buffer writer must return at least the number of bytes requested.");
        }
    }

    /// <summary>
    /// The number of bytes to ask the destination for. Each document length is known. Thus the
    /// writer knows the number of bytes that the outermost document still needs, and it asks for
    /// that number. A destination that grows by doubling can then take its size one time. This
    /// number is only a hint. A destination that gives fewer bytes still works.
    /// </summary>
    private int SizeHint(int minimum)
    {
        if (_depth == 0)
            return minimum;

        var outermost = _frames[0];
        var remaining = outermost.ExpectedLength - (_position - outermost.StartPosition);

        if (remaining < minimum)
            return minimum;

        return remaining > MaxSizeHint ? MaxSizeHint : (int)remaining;
    }

    /// <summary>
    /// Commits the buffered bytes to the destination.
    /// </summary>
    /// <remarks>
    /// You need this method only for a document that you abandon. <see cref="WriteEndDocument"/>
    /// on the top-level document already commits.
    /// </remarks>
    public void Flush()
    {
        if (_buffered > 0)
        {
            _output.Advance(_buffered);
            _buffered = 0;
        }

        // The contract of IBufferWriter is that the buffer is invalid after Advance.
        _memory = default;
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
