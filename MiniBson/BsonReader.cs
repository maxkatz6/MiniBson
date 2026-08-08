using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiniBson;

/// <summary>
/// A low-level, forward-only BSON reader over caller memory.
/// </summary>
/// <remarks>
/// <para>
/// The reader does not copy the input and does not own it. It is a <see langword="ref"/>
/// <see langword="struct"/> for that reason. The compiler then keeps the reader, and each span it
/// returns, inside the lifetime of the input. This gives the reader the same rules as
/// <c>Utf8JsonReader</c>. It cannot cross an <c>await</c>, a lambda cannot capture it, and a class
/// cannot hold it in a field.
/// </para>
/// <para>
/// The full document must be in memory before you construct a reader. Read the four-byte length
/// first, wait for that number of bytes, and then give the reader that slice.
/// </para>
/// </remarks>
#if MINIBSON_PUBLIC
public ref struct BsonReader
#else
internal ref struct BsonReader
#endif
{
    /// <summary>
    /// The smallest length a JavaScript-with-scope value can declare: its own length prefix, an
    /// empty string, and an empty document.
    /// </summary>
    private const int JavaScriptWithScopeOverhead = 4 + 5 + BsonSize.DocumentOverhead;

    // The current segment and the position in it. Each byte that the reader consumes comes from
    // here. For input that is one piece, this is the full input, and the segment fields below do
    // nothing.
    private ReadOnlySpan<byte> _span;
    private int _index;

    /// <summary>The offset of <c>_span[0]</c> in the full input.</summary>
    private long _segmentStart;

    // The same bytes as _span, for an input form that has memory behind it. A reader from a plain
    // span has no such memory. Thus ReadBinaryMemory copies there and in no other case.
    private ReadOnlyMemory<byte> _segmentMemory;
    private bool _hasMemory;

    // Multi-segment input. _next positions the segment after _span.
    private ReadOnlySequence<byte> _sequence;
    private SequencePosition _next;
    private bool _isMultiSegment;

    /// <summary>The length of the full input.</summary>
    private long _end;

    /// <summary>
    /// The end of the outermost open document, or <see cref="_end"/> when no document is open.
    /// One test against this value covers the end of the document and the end of the input. Thus
    /// no read tests them separately.
    /// </summary>
    private long _limit;

    // The open documents, packed as (endPosition << 1) | isArray. Depth 1 is a field, so a flat
    // document allocates nothing. Only a nested document uses the array.
    private long _frame0;
    private long[]? _frames;
    private int _depth;

    private BsonType _type;
    private ReadOnlySpan<byte> _nameSpan;
    private string? _name;

    /// <summary>
    /// Reads from a span. This form is the fastest. It is also the only form where
    /// <see cref="ReadBinaryMemory"/> copies.
    /// </summary>
    public BsonReader(ReadOnlySpan<byte> data)
    {
        this = default;
        _span = data;
        _end = data.Length;
        _limit = _end;
    }

    /// <summary>
    /// Reads from memory. <see cref="ReadBinaryMemory"/> returns a slice of it and makes no copy.
    /// </summary>
    public BsonReader(ReadOnlyMemory<byte> data)
    {
        this = default;
        _segmentMemory = data;
        _span = data.Span;
        _hasMemory = true;
        _end = data.Length;
        _limit = _end;
    }

    /// <summary>Reads from an array.</summary>
    public BsonReader(byte[] data)
        : this(new ReadOnlyMemory<byte>(data ?? throw new ArgumentNullException(nameof(data))))
    {
    }

    /// <summary>
    /// Reads from a sequence, which is the form that a <c>PipeReader</c> gives. The reader joins
    /// a value that lies across two segments. A value inside one segment costs nothing more.
    /// </summary>
    public BsonReader(in ReadOnlySequence<byte> data)
    {
        this = default;
        _end = data.Length;
        _limit = _end;

        if (data.IsSingleSegment)
        {
            // The input is one piece. Thus the segment code below does not run.
            _segmentMemory = data.First;
            _span = _segmentMemory.Span;
            _hasMemory = true;
            return;
        }

        _sequence = data;
        _isMultiSegment = true;
        _next = data.Start;

        // The first segment is allowed to be empty. This call finds the first one that is not.
        MoveNextSegment();
    }

    /// <summary>
    /// The type of the current element after a call to <see cref="Read"/>.
    /// </summary>
    public readonly BsonType CurrentType => _type;

    /// <summary>
    /// The name of the current element after a call to <see cref="Read"/>. The reader decodes it
    /// when you first read this property. Thus code that skips an element does not pay for its
    /// name.
    /// </summary>
    public string CurrentName => _name ??= DecodeString(_nameSpan);

    /// <summary>
    /// The name of the current element as UTF-8 bytes. It allocates no memory, except for a name
    /// that lies across two segments.
    /// </summary>
    public readonly ReadOnlySpan<byte> CurrentNameSpan => _nameSpan;

    /// <summary>
    /// True when the reader is in an array.
    /// </summary>
    public readonly bool IsInArray => _depth > 0 && (PeekFrame() & 1) != 0;

    /// <summary>
    /// The number of bytes that the reader consumed. Slice your input at this value to read the
    /// document after this one.
    /// </summary>
    public readonly long BytesConsumed => _segmentStart + _index;

    /// <summary>
    /// Reads the start of a document. Call this method before you read the elements.
    /// </summary>
    public void ReadStartDocument() => PushDocument(isArray: false);

    /// <summary>
    /// Reads the start of an embedded document.
    /// </summary>
    public void ReadStartNestedDocument()
    {
        EnsureType(BsonType.Document);
        PushDocument(isArray: false);
    }

    /// <summary>
    /// Reads the start of an array.
    /// </summary>
    public void ReadStartArray()
    {
        EnsureType(BsonType.Array);
        PushDocument(isArray: true);
    }

    /// <summary>
    /// Reads the end of a document.
    /// </summary>
    public void ReadEndDocument()
    {
        if (_depth == 0)
            throw new InvalidOperationException("No document to end.");

        var endPosition = PeekEnd();
        _depth--;

        // Move forward to the terminator, across each element that the caller did not read. The
        // reader stops one byte before the end, and the code below tests that byte.
        Advance(endPosition - 1 - BytesConsumed);

        var endMarker = ReadByteCore();
        if (endMarker != 0)
            throw new InvalidDataException($"Expected end of document marker (0x00), got 0x{endMarker:X2}");

        // The reader left the outermost document. Thus the next document in the same input can
        // set its own end.
        if (_depth == 0)
            _limit = _end;
    }

    /// <summary>
    /// Reads the end of an array. An array is a document on the wire. Thus this method is
    /// <see cref="ReadEndDocument"/> with the name that agrees with
    /// <see cref="ReadStartArray"/> and with <c>BsonWriter.WriteEndArray</c>.
    /// </summary>
    public void ReadEndArray() => ReadEndDocument();

    /// <summary>
    /// Reads the length prefix of a document and opens a frame for it.
    /// </summary>
    private void PushDocument(bool isArray)
    {
        var start = BytesConsumed;
        var length = ReadLengthCore(BsonSize.DocumentOverhead, "A document");
        var endPosition = start + length; // length counts its own four bytes

        if (_depth == 0)
        {
            // Without this test, a document that declares more bytes than the input holds fails
            // later, inside some other value, with a message that does not name the true cause.
            if (endPosition > _end)
            {
                throw new InvalidDataException(
                    $"A document declares {length} bytes, but the input holds {_end - start}.");
            }

            _limit = endPosition;
        }
        else if (endPosition > PeekEnd())
        {
            // Without this test, a nested length that is too large lets a read or a skip go past
            // the end of the outer document and read the wrong bytes.
            throw new InvalidDataException(
                $"A nested document declares {length} bytes, which does not fit in the document containing it.");
        }

        PushFrame(endPosition, isArray);
    }

    /// <summary>
    /// Reads the header of the next element. Returns true if there is an element. Returns false
    /// at the end of the document.
    /// </summary>
    public bool Read()
    {
        if (_depth == 0)
            throw new InvalidOperationException("Not inside a document. Call ReadStartDocument() first.");

        // Test for the end of the document.
        if (BytesConsumed >= PeekEnd() - 1)
        {
            _type = default;
            _nameSpan = default;
            _name = string.Empty;
            return false;
        }

        _type = (BsonType)ReadByteCore();

        if (_type == 0) // End of document marker
        {
            _nameSpan = default;
            _name = string.Empty;
            return false;
        }

        _nameSpan = TakeCString();
        _name = null; // CurrentName decodes it, if the caller reads that property
        return true;
    }

    /// <summary>
    /// Reads a boolean value.
    /// </summary>
    public bool ReadBoolean()
    {
        EnsureType(BsonType.Boolean);
        return ReadByteCore() != 0;
    }

    // The number readers accept all three number types on the wire and convert between them.
    // Thus a model with a wider or a narrower property can still read an older document. The
    // switch is the full type test. An EnsureType call before it would give the same list of
    // types a second time.

    /// <summary>
    /// Reads a 32-bit integer value.
    /// </summary>
    public int ReadInt32() => CurrentType switch
    {
        BsonType.Int32 => ReadInt32Core(),
        BsonType.Int64 => (int)ReadInt64Core(),
        BsonType.Double => (int)ReadDoubleCore(),
        _ => throw UnexpectedType(BsonType.Int32, BsonType.Int64, BsonType.Double)
    };

    /// <summary>
    /// Reads a 64-bit integer value.
    /// </summary>
    public long ReadInt64() => CurrentType switch
    {
        BsonType.Int64 => ReadInt64Core(),
        BsonType.Int32 => ReadInt32Core(),
        BsonType.Double => (long)ReadDoubleCore(),
        _ => throw UnexpectedType(BsonType.Int64, BsonType.Int32, BsonType.Double)
    };

    /// <summary>
    /// Reads a double value.
    /// </summary>
    public double ReadDouble() => CurrentType switch
    {
        BsonType.Double => ReadDoubleCore(),
        BsonType.Int32 => ReadInt32Core(),
        BsonType.Int64 => ReadInt64Core(),
        _ => throw UnexpectedType(BsonType.Double, BsonType.Int32, BsonType.Int64)
    };

    /// <summary>
    /// Reads a string value.
    /// </summary>
    public string ReadString()
    {
        EnsureType(BsonType.String, BsonType.JavaScript, BsonType.Symbol);
        return ReadLengthPrefixedString();
    }

    /// <summary>
    /// Reads a DateTime value.
    /// </summary>
    public DateTime ReadDateTime()
    {
        EnsureType(BsonType.DateTime);
        var milliseconds = ReadInt64Core();
        return UnixEpoch.AddMilliseconds(milliseconds);
    }

    /// <summary>
    /// Reads a BSON ObjectId (12 bytes). The span points into your input unless the value lay
    /// across two segments.
    /// </summary>
    public ReadOnlySpan<byte> ReadObjectId()
    {
        EnsureType(BsonType.ObjectId);
        return TakeContiguous(BsonSize.ObjectId);
    }

    /// <summary>
    /// Reads a BSON ObjectId into the span that you supply.
    /// </summary>
    public void ReadObjectId(Span<byte> destination)
    {
        EnsureType(BsonType.ObjectId);
        if (destination.Length < BsonSize.ObjectId)
            throw new ArgumentException($"Destination must be at least {BsonSize.ObjectId} bytes.", nameof(destination));

        ReadIntoCore(destination.Slice(0, BsonSize.ObjectId));
    }

    /// <summary>
    /// Reads binary data. The span points into your input and copies nothing, unless the value
    /// lay across two segments.
    /// </summary>
    public ReadOnlySpan<byte> ReadBinary(out BsonBinarySubType subType)
    {
        subType = ReadBinaryHeader(out var dataLength);
        return TakeContiguous(dataLength);
    }

    /// <summary>
    /// Reads binary data into a new array. Use it when the value must outlive your input.
    /// </summary>
    public byte[] ReadBinaryArray(out BsonBinarySubType subType)
    {
        subType = ReadBinaryHeader(out var dataLength);

        if (dataLength == 0)
            return [];

        var data = new byte[dataLength];
        ReadIntoCore(data);
        return data;
    }

    /// <summary>
    /// Reads binary data as a <see cref="ReadOnlyMemory{T}"/>. It is a slice of your input, with
    /// no copy, for every constructor except <see cref="BsonReader(ReadOnlySpan{byte})"/> — a
    /// span has no memory behind it to slice. A value across two segments is also copied.
    /// </summary>
    public ReadOnlyMemory<byte> ReadBinaryMemory(out BsonBinarySubType subType)
    {
        subType = ReadBinaryHeader(out var dataLength);
        return TakeMemory(dataLength);
    }

    /// <summary>
    /// Consumes the length prefix and the subtype byte of a binary value. The reader then
    /// points at the data.
    /// </summary>
    /// <param name="dataLength">
    /// The length of the data. The deprecated <see cref="BsonBinarySubType.BinaryOld"/> subtype
    /// gives the length a second time inside the data, and that inner length is the correct one.
    /// </param>
    private BsonBinarySubType ReadBinaryHeader(out int dataLength)
    {
        EnsureType(BsonType.Binary);
        dataLength = ReadLengthCore(0, "A binary value");
        var subType = (BsonBinarySubType)ReadByteCore();

        if (subType == BsonBinarySubType.BinaryOld)
            dataLength = ReadLengthCore(0, "A binary value");

        EnsureWithinLimit(dataLength);

        return subType;
    }

    /// <summary>
    /// Reads a GUID from binary data.
    /// </summary>
    public Guid ReadGuid()
    {
        var data = ReadBinary(out _);
        if (data.Length != 16)
            throw new InvalidDataException($"Expected 16 bytes for GUID, got {data.Length}.");

#if NET6_0_OR_GREATER
        return new Guid(data);
#else
        return new Guid(data.ToArray());
#endif
    }

    /// <summary>
    /// Reads a regular expression.
    /// </summary>
    public (string Pattern, string Options) ReadRegex()
    {
        EnsureType(BsonType.Regex);
        var pattern = DecodeString(TakeCString());
        var options = DecodeString(TakeCString());
        return (pattern, options);
    }

    /// <summary>
    /// Reads JavaScript code.
    /// </summary>
    public string ReadJavaScript()
    {
        EnsureType(BsonType.JavaScript);
        return ReadLengthPrefixedString();
    }

    private string ReadLengthPrefixedString()
    {
        // The declared length includes the value and its terminator.
        var valueLength = ReadLengthCore(1, "A string") - 1;
        var value = DecodeString(TakeContiguous(valueLength));
        ExpectStringTerminator();
        return value;
    }

    private void ExpectStringTerminator()
    {
        var terminator = ReadByteCore();
        if (terminator != 0)
            throw new InvalidDataException($"Expected a string terminator (0x00), got 0x{terminator:X2}.");
    }

    /// <summary>
    /// Reads a BSON timestamp.
    /// </summary>
    public (uint Increment, uint Timestamp) ReadTimestamp()
    {
        EnsureType(BsonType.Timestamp);
        var increment = ReadUInt32Core();
        var timestamp = ReadUInt32Core();
        return (increment, timestamp);
    }

    /// <summary>
    /// Skips the value of the current element.
    /// </summary>
    /// <remarks>
    /// This method accepts each type that <see cref="BsonType"/> names. This includes the
    /// deprecated types that this reader has no accessor for. Generated deserializers skip
    /// each element that they do not know. Thus a type that is absent here is not one bad
    /// element. The reader cannot read the document after that element.
    /// </remarks>
    public void Skip()
    {
        switch (CurrentType)
        {
            case BsonType.Double:
            case BsonType.DateTime:
            case BsonType.Timestamp:
            case BsonType.Int64:
                Advance(8);
                break;
            case BsonType.String:
            case BsonType.JavaScript:
            case BsonType.Symbol:
                var stringLength = ReadLengthCore(1, "A string");
                Advance(stringLength);
                break;
            case BsonType.Document:
            case BsonType.Array:
                var docLength = ReadLengthCore(BsonSize.DocumentOverhead, "A document");
                Advance(docLength - 4);
                break;
            case BsonType.Binary:
                var binLength = ReadLengthCore(0, "A binary value");
                Advance(1L + binLength); // subtype + data
                break;
            case BsonType.ObjectId:
                Advance(BsonSize.ObjectId);
                break;
            case BsonType.Boolean:
                Advance(1);
                break;
            case BsonType.Null:
            case BsonType.Undefined:
            case BsonType.MinKey:
            case BsonType.MaxKey:
                // There is no data to skip.
                break;
            case BsonType.Regex:
                TakeCString(); // pattern
                TakeCString(); // options
                break;
            case BsonType.DBPointer:
                // A deprecated type. It has a string and then a 12-byte ObjectId.
                var pointerLength = ReadLengthCore(1, "A string");
                Advance((long)pointerLength + BsonSize.ObjectId);
                break;
            case BsonType.Int32:
                Advance(4);
                break;
            case BsonType.JavaScriptWithScope:
                var scopeLength = ReadLengthCore(JavaScriptWithScopeOverhead, "A JavaScript-with-scope value");
                Advance(scopeLength - 4);
                break;
            case BsonType.Decimal128:
                Advance(16);
                break;
            default:
                // This reader does not know this type byte. Thus the length of the value is
                // unknown, and the reader would use the wrong offsets for the other elements.
                throw new InvalidDataException(
                    $"Cannot skip BSON type 0x{(byte)CurrentType:X2}: it is not a known type, so its length is undefined.");
        }
    }

    /// <summary>
    /// Reads any value and returns it as an object.
    /// </summary>
    public object? ReadValue()
    {
        return CurrentType switch
        {
            BsonType.Double => ReadDouble(),
            BsonType.String => ReadString(),
            BsonType.Document => ReadDocumentAsDictionary(),
            BsonType.Array => ReadArrayAsList(),
            BsonType.Binary => ReadBinaryArray(out _),
            BsonType.Undefined => null,
            BsonType.ObjectId => ReadObjectId().ToArray(),
            BsonType.Boolean => ReadBoolean(),
            BsonType.DateTime => ReadDateTime(),
            BsonType.Null => null,
            BsonType.Regex => ReadRegex(),
            BsonType.JavaScript => ReadJavaScript(),
            BsonType.Symbol => ReadString(),
            BsonType.Int32 => ReadInt32(),
            BsonType.Timestamp => ReadTimestamp(),
            BsonType.Int64 => ReadInt64(),
            _ => throw new InvalidDataException($"Unsupported BSON type: {CurrentType}")
        };
    }

    private Dictionary<string, object?> ReadDocumentAsDictionary()
    {
        var dict = new Dictionary<string, object?>();
        ReadStartNestedDocument();
        while (Read())
        {
            dict[CurrentName] = ReadValue();
        }
        ReadEndDocument();
        return dict;
    }

    private List<object?> ReadArrayAsList()
    {
        var list = new List<object?>();
        ReadStartArray();
        while (Read())
        {
            list.Add(ReadValue());
        }
        ReadEndDocument();
        return list;
    }

    // The open-document frames. Only these four members touch the packing, so a different
    // container for them is a local change.

    private void PushFrame(long endPosition, bool isArray)
    {
        var packed = (endPosition << 1) | (isArray ? 1L : 0L);

        if (_depth == 0)
        {
            _frame0 = packed;
        }
        else
        {
            var index = _depth - 1;

            if (_frames is null)
                _frames = new long[8];
            else if (index >= _frames.Length)
                Array.Resize(ref _frames, _frames.Length * 2);

            _frames[index] = packed;
        }

        _depth++;
    }

    private readonly long PeekFrame() => _depth == 1 ? _frame0 : _frames![_depth - 2];

    private readonly long PeekEnd() => PeekFrame() >> 1;

    private void EnsureType(BsonType expected)
    {
        if (CurrentType != expected)
            throw UnexpectedType(expected);
    }

    private void EnsureType(BsonType first, BsonType second, BsonType third)
    {
        if (CurrentType != first && CurrentType != second && CurrentType != third)
            throw UnexpectedType(first, second, third);
    }

    /// <summary>
    /// The one method that makes the message for a wrong type. It takes a
    /// <see langword="params"/> array, which allocates memory. Only the failure path allocates
    /// it, and there the exception costs more than the array.
    /// </summary>
    private readonly InvalidOperationException UnexpectedType(params BsonType[] expected) =>
        new(expected.Length == 1
            ? $"Expected {expected[0]}, but current type is {_type}."
            : $"Expected one of [{string.Join(", ", expected)}], but current type is {_type}.");

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string DecodeString(ReadOnlySpan<byte> value)
    {
#if NET6_0_OR_GREATER
        return Encoding.UTF8.GetString(value);
#else
        return value.IsEmpty ? string.Empty : Encoding.UTF8.GetString(value.ToArray());
#endif
    }

    // The read primitives. Each byte that the reader consumes goes through one of these methods.
    // They are also the only members that know whether the input is one piece or many. Thus each
    // read method above has one body and does not test the shape of the input itself.

    /// <summary>
    /// Rejects a read that would go past the open document or past the input. One test covers
    /// both, because <c>_limit</c> is the nearer of the two ends.
    /// </summary>
    private readonly void EnsureWithinLimit(long count)
    {
        if (BytesConsumed + count > _limit)
        {
            throw new InvalidDataException(
                $"A value of {count} bytes does not fit in the {_limit - BytesConsumed} bytes that remain " +
                "in the document. The input is malformed or truncated.");
        }
    }

    /// <summary>
    /// Moves to the next segment that is not empty. A sequence is allowed to hold an empty
    /// segment at any place, including the first place and the last.
    /// </summary>
    private void MoveNextSegment()
    {
        // This keeps the position correct. _index returns to zero, and _segmentStart takes the
        // length of the segment that the reader leaves.
        _segmentStart += _span.Length;

        while (_sequence.TryGet(ref _next, out var memory, advance: true))
        {
            if (memory.Length == 0)
                continue;

            _segmentMemory = memory;
            _span = memory.Span;
            _hasMemory = true;
            _index = 0;
            return;
        }

        _segmentMemory = default;
        _span = default;
        _index = 0;
    }

    /// <summary>
    /// Returns <paramref name="count"/> adjacent bytes at the position of the reader, and does
    /// not consume them. Returns false when the value crosses a segment boundary.
    /// </summary>
    private bool TryPeekContiguous(int count, out ReadOnlySpan<byte> span)
    {
        EnsureWithinLimit(count);

        if (_index == _span.Length && _isMultiSegment)
            MoveNextSegment();

        if (_span.Length - _index >= count)
        {
            span = _span.Slice(_index, count);
            return true;
        }

        span = default;
        return false;
    }

    /// <summary>
    /// Consumes exactly <c>destination.Length</c> bytes, across as many segments as it takes.
    /// </summary>
    /// <remarks>
    /// <paramref name="destination"/> is <see langword="scoped"/>, so a caller can pass a
    /// <see langword="stackalloc"/> buffer. Without that keyword the compiler must assume that
    /// the reader keeps the span, and the span could then outlive its stack frame.
    /// </remarks>
    private void ReadIntoCore(scoped Span<byte> destination)
    {
        EnsureWithinLimit(destination.Length);

        var written = 0;
        while (written < destination.Length)
        {
            if (_index == _span.Length)
            {
                if (!_isMultiSegment)
                    throw EndOfInput(destination.Length, written);

                MoveNextSegment();

                if (_span.Length == 0)
                    throw EndOfInput(destination.Length, written);
            }

            var chunk = Math.Min(destination.Length - written, _span.Length - _index);
            _span.Slice(_index, chunk).CopyTo(destination.Slice(written));
            written += chunk;
            _index += chunk;
        }
    }

    /// <summary>
    /// Consumes <paramref name="count"/> adjacent bytes and returns them as a slice of the input, with no copy.
    /// Returns false when the value crosses a segment boundary, and then consumes nothing, so the caller can read the value a second way.
    /// </summary>
    private bool TryTakeContiguous(int count, out ReadOnlySpan<byte> span)
    {
        if (!TryPeekContiguous(count, out span))
            return false;

        _index += count;
        return true;
    }

    /// <summary>
    /// Consumes <paramref name="count"/> bytes and returns them as adjacent bytes. The result is
    /// a slice of your input, with no copy, except for a value that crosses a segment boundary.
    /// </summary>
    private ReadOnlySpan<byte> TakeContiguous(int count)
    {
        if (count == 0)
            return default;

        if (TryTakeContiguous(count, out var span))
            return span;

        // The value is in more than one piece. The result leaves this method, so the buffer
        // cannot come from a pool. The reader has no Dispose that could return it.
        var copy = new byte[count];
        ReadIntoCore(copy);
        return copy;
    }

    /// <summary>
    /// Consumes <paramref name="count"/> bytes as memory. The result is a slice of your input,
    /// except when the reader has no memory behind it or the value crosses a segment boundary.
    /// </summary>
    private ReadOnlyMemory<byte> TakeMemory(int count)
    {
        if (count == 0)
            return default;

        EnsureWithinLimit(count);

        if (_hasMemory && TryPeekContiguous(count, out _))
        {
            var slice = _segmentMemory.Slice(_index, count);
            _index += count;
            return slice;
        }

        var copy = new byte[count];
        ReadIntoCore(copy);
        return copy;
    }

    private byte ReadByteCore()
    {
        EnsureWithinLimit(1);

        if (_index == _span.Length && _isMultiSegment)
            MoveNextSegment();

        if (_index == _span.Length)
            throw EndOfInput(1, 0);

        return _span[_index++];
    }

    /// <summary>
    /// Moves the position forward and reads nothing. The reader uses it to skip a value, and to
    /// reach the terminator of a document that has unread elements.
    /// </summary>
    private void Advance(long count)
    {
        if (count == 0)
            return;

        if (count < 0)
        {
            // A declared length that is too short gives a negative distance. Without this test
            // the reader would move backwards, and each offset after this point would be wrong.
            throw new InvalidDataException(
                $"A declared length would move the reader {-count} bytes backwards. The input is malformed.");
        }

        EnsureWithinLimit(count);

        while (count > 0)
        {
            if (_index == _span.Length)
            {
                if (!_isMultiSegment)
                    throw EndOfInput(count, 0);

                MoveNextSegment();

                if (_span.Length == 0)
                    throw EndOfInput(count, 0);
            }

            var chunk = (int)Math.Min(count, _span.Length - _index);
            _index += chunk;
            count -= chunk;
        }
    }

    /// <summary>
    /// Consumes a string with a null terminator and returns its bytes without that terminator.
    /// An element name and each part of a regular expression use this form.
    /// </summary>
    private ReadOnlySpan<byte> TakeCString()
    {
        // The search stops at the end of the document. Without that limit, a name with no
        // terminator would take the bytes of the document after this one.
        var searchable = (int)Math.Min(_span.Length - _index, _limit - BytesConsumed);

        if (searchable > 0)
        {
            var index = _span.Slice(_index, searchable).IndexOf((byte)0);
            if (index >= 0)
            {
                var value = _span.Slice(_index, index);
                _index += index + 1; // past the terminator
                return value;
            }
        }

        if (!_isMultiSegment)
            throw new InvalidDataException("Unterminated cstring.");

        return TakeCStringAcrossSegments();
    }

    /// <summary>
    /// Collects the rest of a cstring across segments. The result leaves this method. Thus it
    /// goes into a new array and not into a pooled one.
    /// </summary>
    private ReadOnlySpan<byte> TakeCStringAcrossSegments()
    {
        var scratch = new byte[128];
        var length = 0;

        while (true)
        {
            if (_index == _span.Length)
            {
                MoveNextSegment();
                if (_span.Length == 0)
                    throw new InvalidDataException("Unterminated cstring.");
            }

            var searchable = (int)Math.Min(_span.Length - _index, _limit - BytesConsumed);
            if (searchable == 0)
                throw new InvalidDataException("Unterminated cstring.");

            var index = _span.Slice(_index, searchable).IndexOf((byte)0);
            var take = index >= 0 ? index : searchable;

            if (length + take > scratch.Length)
                Array.Resize(ref scratch, Math.Max(scratch.Length * 2, length + take));

            _span.Slice(_index, take).CopyTo(scratch.AsSpan(length));
            length += take;
            _index += take;

            if (index >= 0)
            {
                _index++; // past the terminator
                return new ReadOnlySpan<byte>(scratch, 0, length);
            }
        }
    }

    private int ReadInt32Core()
    {
        if (TryTakeContiguous(4, out var span))
            return BinaryPrimitives.ReadInt32LittleEndian(span);

        Span<byte> scratch = stackalloc byte[4];
        ReadIntoCore(scratch);
        return BinaryPrimitives.ReadInt32LittleEndian(scratch);
    }

    private uint ReadUInt32Core()
    {
        if (TryTakeContiguous(4, out var span))
            return BinaryPrimitives.ReadUInt32LittleEndian(span);

        Span<byte> scratch = stackalloc byte[4];
        ReadIntoCore(scratch);
        return BinaryPrimitives.ReadUInt32LittleEndian(scratch);
    }

    private long ReadInt64Core()
    {
        if (TryTakeContiguous(8, out var span))
            return BinaryPrimitives.ReadInt64LittleEndian(span);

        Span<byte> scratch = stackalloc byte[8];
        ReadIntoCore(scratch);
        return BinaryPrimitives.ReadInt64LittleEndian(scratch);
    }

    // The raw bits keep this value little-endian on all machines, the same as BsonWriter.
    private double ReadDoubleCore() => BitConverter.Int64BitsToDouble(ReadInt64Core());

    /// <summary>
    /// Reads a length prefix and rejects a length that no valid value can have. Thus a bad
    /// length fails here. It does not put each byte after it at the wrong offset.
    /// </summary>
    private int ReadLengthCore(int minimum, string what)
    {
        var length = ReadInt32Core();
        if (length < minimum)
        {
            throw new InvalidDataException(
                $"{what} declares a length of {length} bytes; the minimum is {minimum}.");
        }

        return length;
    }

    private readonly InvalidDataException EndOfInput(long expected, int got) =>
        new($"Expected {expected} bytes but the input ended after {got}.");
}
