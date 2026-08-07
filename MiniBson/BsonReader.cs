using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniBson;

/// <summary>
/// A low-level, forward-only BSON reader.
/// </summary>
#if MINIBSON_PUBLIC
public sealed class BsonReader : IDisposable
#else
internal sealed class BsonReader : IDisposable
#endif
{
    private const int WindowSize = 8192;

    /// <summary>
    /// The minimum length that a JavaScript-with-scope value can declare. It has its own
    /// int32, an empty string, and an empty document.
    /// </summary>
    private const int JavaScriptWithScopeOverhead = 4 + 5 + BsonSize.DocumentOverhead;

    private readonly Stream? _stream;
    private readonly bool _leaveOpen;
    private readonly bool _canSeek;

    /// <summary>
    /// True when <see cref="_buffer"/> is the caller's own memory and not a rented window.
    /// Such a buffer already holds the full input, and nothing can refill it. Thus the reader
    /// can return a slice of it and make no copy.
    /// </summary>
    private readonly bool _bufferIsSource;

    // The bytes available to read, as the half-open range [_start, _end) of _buffer. All bytes
    // that this reader consumes come from here. The reader uses the stream only to refill the
    // window. Thus a different source (ReadOnlySequence, PipeReader) is a change to the refill
    // path and not a change to each read method.
    private byte[] _buffer;
    private int _start;
    private int _end;

    private readonly Stack<DocumentContext> _contextStack = new();

    // A count of the bytes that the reader consumed. The reader counts them and does not ask
    // the stream, because a stream that cannot seek gives no answer. The reader also finds the
    // end of each document with this count.
    private long _position;

    // The end of the outermost open document, or -1 when no document is open. No read goes
    // past it. Thus a stream that holds a sequence of documents stays readable one document at
    // a time.
    private long _readLimit = -1;

    private bool _disposed;

    private struct DocumentContext
    {
        public long EndPosition;
        public bool IsArray;
    }

    public BsonReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        _canSeek = stream.CanSeek;
        _buffer = ArrayPool<byte>.Shared.Rent(WindowSize);
        _bufferIsSource = false;
    }

    public BsonReader(byte[] data)
        : this(new ReadOnlyMemory<byte>(data ?? throw new ArgumentNullException(nameof(data))))
    {
    }

    public BsonReader(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            _buffer = segment.Array;
            _start = segment.Offset;
            _end = segment.Offset + segment.Count;
        }
        else
        {
            _buffer = data.ToArray();
            _start = 0;
            _end = _buffer.Length;
        }

        _bufferIsSource = true;
        _leaveOpen = true;
    }

    /// <summary>
    /// The type of the current element after a call to Read().
    /// </summary>
    public BsonType CurrentType { get; private set; }

    /// <summary>
    /// The name of the current element after a call to Read().
    /// </summary>
    public string CurrentName { get; private set; } = string.Empty;

    /// <summary>
    /// True when the reader is in an array.
    /// </summary>
    public bool IsInArray => _contextStack.Count > 0 && _contextStack.Peek().IsArray;

    /// <summary>
    /// Reads the start of a document. Call this method before you read the elements.
    /// </summary>
    public void ReadStartDocument() => PushDocument(isArray: false);

    /// <summary>
    /// Reads the end of a document.
    /// </summary>
    public void ReadEndDocument()
    {
        if (_contextStack.Count == 0)
            throw new InvalidOperationException("No document to end.");

        var context = _contextStack.Pop();

        // Move forward to the terminator, across each element that the caller did not read.
        // The reader stops one byte before the end, and the code below tests that byte.
        Advance(context.EndPosition - 1 - _position);

        var endMarker = ReadByteCore();
        if (endMarker != 0)
            throw new InvalidDataException($"Expected end of document marker (0x00), got 0x{endMarker:X2}");

        // The reader left the outermost document. Thus the next document can declare its own end.
        if (_contextStack.Count == 0)
            _readLimit = -1;
    }

    /// <summary>
    /// Reads the end of an array. An array is a document on the wire. Thus this method is
    /// <see cref="ReadEndDocument"/> with the name that agrees with
    /// <see cref="ReadStartArray"/> and with <c>BsonWriter.WriteEndArray</c>.
    /// </summary>
    public void ReadEndArray() => ReadEndDocument();

    /// <summary>
    /// Reads the length prefix of a document and opens a <c>DocumentContext</c> for it.
    /// </summary>
    private void PushDocument(bool isArray)
    {
        var length = ReadLengthCore(BsonSize.DocumentOverhead, "A document");
        var endPosition = _position + length - 4; // -4 because length includes itself

        if (_contextStack.Count == 0)
        {
            _readLimit = endPosition;
        }
        else if (endPosition > _contextStack.Peek().EndPosition)
        {
            // Without this test, a nested length that is too large lets a read or a skip go
            // past the end of the outer document and read the wrong bytes.
            throw new InvalidDataException(
                $"A nested document declares {length} bytes, which does not fit in the document containing it.");
        }

        _contextStack.Push(new DocumentContext { EndPosition = endPosition, IsArray = isArray });
    }

    /// <summary>
    /// Reads the header of the next element. Returns true if there is an element. Returns
    /// false at the end of the document.
    /// </summary>
    public bool Read()
    {
        if (_contextStack.Count == 0)
            throw new InvalidOperationException("Not inside a document. Call ReadStartDocument() first.");

        var context = _contextStack.Peek();

        // Test for the end of the document.
        if (_position >= context.EndPosition - 1)
        {
            CurrentType = default;
            CurrentName = string.Empty;
            return false;
        }

        CurrentType = (BsonType)ReadByteCore();

        if (CurrentType == 0) // End of document marker
        {
            CurrentName = string.Empty;
            return false;
        }

        CurrentName = ReadCString();
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
    /// Reads a BSON ObjectId (12 bytes).
    /// </summary>
    public byte[] ReadObjectId()
    {
        EnsureType(BsonType.ObjectId);
        return Take(BsonSize.ObjectId).ToArray();
    }

    /// <summary>
    /// Reads a BSON ObjectId into the span that you supply.
    /// </summary>
    public void ReadObjectId(Span<byte> destination)
    {
        EnsureType(BsonType.ObjectId);
        if (destination.Length < BsonSize.ObjectId)
            throw new ArgumentException($"Destination must be at least {BsonSize.ObjectId} bytes.", nameof(destination));

        Take(BsonSize.ObjectId).CopyTo(destination);
    }

    /// <summary>
    /// Reads binary data.
    /// </summary>
    public (byte[] Data, BsonBinarySubType SubType) ReadBinary()
    {
        var subType = ReadBinaryHeader(out var dataLength);
        return (ReadBytesCore(dataLength), subType);
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

        return subType;
    }

    /// <summary>
    /// Reads binary data as a <see cref="ReadOnlyMemory{T}"/>. A reader from a
    /// <see cref="byte"/> array, or from a <see cref="ReadOnlyMemory{T}"/> with an array behind
    /// it, returns a slice of your memory and allocates nothing. A reader from a stream copies
    /// the data into a new array. The slice points at your memory, so you see each change that
    /// you make to it.
    /// </summary>
    public (ReadOnlyMemory<byte> Data, BsonBinarySubType SubType) ReadBinaryAsMemory()
    {
        var subType = ReadBinaryHeader(out var dataLength);

        if (_bufferIsSource)
        {
            EnsureWithinDocument(dataLength);
            if (dataLength > _end - _start)
                throw new EndOfStreamException($"Expected {dataLength} bytes but the input ended after {_end - _start}.");

            var slice = new ReadOnlyMemory<byte>(_buffer, _start, dataLength);
            _start += dataLength;
            _position += dataLength;
            return (slice, subType);
        }

        return (ReadBytesCore(dataLength), subType);
    }

    /// <summary>
    /// Reads a GUID from binary data.
    /// </summary>
    public Guid ReadGuid()
    {
        var (data, _) = ReadBinary();
        if (data.Length != 16)
            throw new InvalidDataException($"Expected 16 bytes for GUID, got {data.Length}.");
        return new Guid(data);
    }

    /// <summary>
    /// Reads a regular expression.
    /// </summary>
    public (string Pattern, string Options) ReadRegex()
    {
        EnsureType(BsonType.Regex);
        var pattern = ReadCString();
        var options = ReadCString();
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

        // The reader decodes the value in the window if the window is large enough, which is
        // the usual condition. The other method copies the bytes into an array and then
        // discards that array.
        if (valueLength <= _end - _start || (_stream is not null && valueLength <= _buffer.Length))
        {
            var value = DecodeString(Take(valueLength));
            ExpectStringTerminator();
            return value;
        }

        var bytes = ReadBytesCore(valueLength);
        ExpectStringTerminator();
        return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
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
                Advance(1 + binLength); // subtype + data
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
                ReadCString(); // pattern
                ReadCString(); // options
                break;
            case BsonType.DBPointer:
                // A deprecated type. It has a string and then a 12-byte ObjectId.
                var pointerLength = ReadLengthCore(1, "A string");
                Advance(pointerLength + BsonSize.ObjectId);
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
            BsonType.Binary => ReadBinary().Data,
            BsonType.Undefined => null,
            BsonType.ObjectId => ReadObjectId(),
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

    /// <summary>
    /// Reads a string with a null terminator. The window usually contains the terminator. Thus
    /// the reader decodes the full name from one span.
    /// </summary>
    private string ReadCString()
    {
        var available = _end - _start;
        if (available > 0)
        {
            var index = new ReadOnlySpan<byte>(_buffer, _start, available).IndexOf((byte)0);
            if (index >= 0)
            {
                EnsureWithinDocument(index + 1);
                var value = DecodeString(new ReadOnlySpan<byte>(_buffer, _start, index));
                _start += index + 1; // skip past null terminator
                _position += index + 1;
                return value;
            }
        }

        if (_stream is null)
        {
            // Nothing can refill a buffer-backed reader. Thus a terminator that is not present
            // means the end of the input. The one other cause is an empty window after a call
            // to Dispose.
            ThrowIfDisposed();
            throw new InvalidDataException("Unterminated cstring.");
        }

        return ReadCStringAcrossRefills();
    }

    /// <summary>
    /// Collects the remainder of a cstring across more than one refill. The reader calls this
    /// method when the window does not contain the terminator.
    /// </summary>
    private string ReadCStringAcrossRefills()
    {
        var scratch = ArrayPool<byte>.Shared.Rent(128);
        var length = 0;
        try
        {
            while (true)
            {
                var available = _end - _start;
                if (available == 0)
                {
                    FillAtLeast(1); // Throws rather than returning an unterminated name.
                    continue;
                }

                var index = new ReadOnlySpan<byte>(_buffer, _start, available).IndexOf((byte)0);
                var take = index >= 0 ? index : available;

                EnsureWithinDocument(index >= 0 ? take + 1 : take);

                if (length + take > scratch.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(Math.Max(scratch.Length * 2, length + take));
                    Buffer.BlockCopy(scratch, 0, larger, 0, length);
                    ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
                    scratch = larger;
                }

                Buffer.BlockCopy(_buffer, _start, scratch, length, take);
                length += take;
                _start += take;
                _position += take;

                if (index >= 0)
                {
                    _start++; // null terminator
                    _position++;
                    return Encoding.UTF8.GetString(scratch, 0, length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }
    }

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
    private InvalidOperationException UnexpectedType(params BsonType[] expected) =>
        new(expected.Length == 1
            ? $"Expected {expected[0]}, but current type is {CurrentType}."
            : $"Expected one of [{string.Join(", ", expected)}], but current type is {CurrentType}.");

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string DecodeString(ReadOnlySpan<byte> value)
    {
#if NET6_0_OR_GREATER
        return Encoding.UTF8.GetString(value);
#else
        return value.IsEmpty ? string.Empty : Encoding.UTF8.GetString(value.ToArray());
#endif
    }

    // The read primitives. All bytes that this reader consumes go through one of these methods.
    // Thus the position stays correct, and the reader does not ask the stream for it.

    private byte ReadByteCore()
    {
        EnsureBuffered(1);
        EnsureWithinDocument(1);

        var value = _buffer[_start++];
        _position++;
        return value;
    }

    private int ReadInt32Core() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    private uint ReadUInt32Core() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    private long ReadInt64Core() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

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

    private byte[] ReadBytesCore(int count)
    {
        if (count == 0)
            return [];

        var bytes = new byte[count];
        ReadExact(bytes, 0, count);
        return bytes;
    }

    /// <summary>
    /// Fills your buffer from the window. It then reads the other bytes directly from the
    /// stream. Thus a value that is longer than the window never goes through the window.
    /// </summary>
    private void ReadExact(byte[] destination, int offset, int count)
    {
        ThrowIfDisposed();
        EnsureWithinDocument(count);

        var buffered = Math.Min(count, _end - _start);
        if (buffered > 0)
        {
            Buffer.BlockCopy(_buffer, _start, destination, offset, buffered);
            _start += buffered;
            _position += buffered;
            offset += buffered;
            count -= buffered;
        }

        while (count > 0)
        {
            if (_stream is null)
                throw new EndOfStreamException($"Expected {count} more bytes but the input ended.");

            var read = _stream.Read(destination, offset, count);
            if (read <= 0)
                throw new EndOfStreamException($"Expected {count} more bytes but the input ended.");

            _position += read;
            offset += read;
            count -= read;
        }
    }

    /// <summary>
    /// Consumes <paramref name="count"/> adjacent bytes from the window. Use this method only
    /// for a value that is not longer than the window. A large value goes through
    /// <see cref="ReadExact"/>.
    /// </summary>
    private ReadOnlySpan<byte> Take(int count)
    {
        EnsureBuffered(count);
        EnsureWithinDocument(count);

        var span = new ReadOnlySpan<byte>(_buffer, _start, count);
        _start += count;
        _position += count;
        return span;
    }

    private void EnsureBuffered(int count)
    {
        if (_end - _start < count)
            FillAtLeast(count);
    }

    /// <summary>
    /// Rejects a call after a call to <see cref="Dispose"/>. This test is on the three methods
    /// that each read reaches. It is not on each public method. <see cref="Dispose"/> empties
    /// the window, so a method with more bytes to return must refill, copy, or skip to get
    /// them. This costs one test for each read instead of one test for each primitive.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BsonReader));
    }

    private void FillAtLeast(int minimum)
    {
        ThrowIfDisposed();
        EnsureWithinDocument(minimum);

        if (_stream is null)
            throw new EndOfStreamException($"Expected {minimum} bytes but the input ended after {_end - _start}.");

        // Each refill starts at the front of the window. Thus the window can hold `minimum`
        // adjacent bytes at each position of the reader.
        var available = _end - _start;
        if (_start > 0)
        {
            if (available > 0)
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, available);

            _start = 0;
            _end = available;
        }

        var target = RefillTarget(minimum);

        while (_end < minimum)
        {
            var read = _stream.Read(_buffer, _end, target - _end);
            if (read <= 0)
                throw new EndOfStreamException($"Expected {minimum} bytes but the input ended after {_end}.");

            _end += read;
        }
    }

    /// <summary>
    /// The number of bytes to put in the window. The reader stops at the end of the outermost
    /// open document. Thus it never consumes the bytes that come after that document on the
    /// stream. It also never reads from a peer that sent one document and now waits.
    /// </summary>
    private int RefillTarget(int minimum)
    {
        // No document is open, so no limit applies. Take only the bytes that the caller asked for.
        if (_readLimit < 0)
            return minimum;

        var remaining = _readLimit - (_position + _end); // _start is 0 by the time this runs
        var target = _end + (int)Math.Min(remaining, _buffer.Length - _end);
        return target < minimum ? minimum : target;
    }

    /// <summary>
    /// Rejects a read that goes past the outermost open document. Only a bad length can fail
    /// this test. If the reader consumed those bytes, it would damage the data that comes
    /// after them.
    /// </summary>
    private void EnsureWithinDocument(long count)
    {
        if (_readLimit >= 0 && _position + count > _readLimit)
        {
            throw new InvalidDataException(
                $"Reading {count} bytes at position {_position} would run " +
                $"{_position + count - _readLimit} bytes past the end of the document.");
        }
    }

    /// <summary>
    /// Moves forward across <paramref name="count"/> bytes. The caller does not see those bytes.
    /// </summary>
    private void Advance(long count)
    {
        if (count == 0)
            return;

        ThrowIfDisposed();

        if (count < 0)
        {
            // Only a bad length prefix comes here. A silent return would put the reader before
            // its own position and make it read the other bytes at the wrong offsets.
            throw new InvalidDataException(
                $"A declared length would move the reader {-count} bytes backwards. The input is malformed.");
        }

        EnsureWithinDocument(count);

        // Take the bytes in the window first. The stream is already past them.
        var buffered = (int)Math.Min(count, _end - _start);
        _start += buffered;
        _position += buffered;
        count -= buffered;

        if (count == 0)
            return;

        if (_stream is null)
            throw new EndOfStreamException($"Expected {count} more bytes but the input ended.");

        if (_canSeek)
        {
            _stream.Position += count;
            _position += count;
            return;
        }

        // The stream cannot seek, so the reader must consume the bytes and discard them. The
        // window is empty now, so the reader uses it for those bytes.
        _start = 0;
        _end = 0;

        while (count > 0)
        {
            var chunk = (int)Math.Min(count, _buffer.Length);
            var read = _stream.Read(_buffer, 0, chunk);
            if (read <= 0)
                throw new EndOfStreamException($"Expected {count} more bytes but the input ended.");

            _position += read;
            count -= read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        var buffered = _end - _start;
        _start = 0;
        _end = 0;

        var buffer = _buffer;
        _buffer = [];

        if (!_bufferIsSource)
        {
            // Clear the array. The pool gives it to the next caller without a change, and it
            // still holds the data that the reader read through it.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (_stream is null)
            return;

        if (!_leaveOpen)
        {
            _stream.Dispose();
            return;
        }

        // The read-ahead put the stream past the bytes that this reader consumed. Return those
        // bytes, so a caller that keeps the stream continues at the correct position.
        //
        // This is the one place that asks the stream about seek support a second time. In each
        // other place, _canSeek is sufficient. But Dispose can run after the caller closed the
        // stream, and a seek on a closed stream throws. That exception would come out of
        // Dispose, and it would be about bytes that nobody can read.
        if (buffered > 0 && _canSeek && _stream.CanSeek)
            _stream.Position -= buffered;
    }
}
