using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniBson;

#if MINIBSON_PUBLIC
/// <summary>
/// A low-level, forward-only BSON reader.
/// </summary>
public sealed class BsonReader : IDisposable
#else
/// <summary>
/// A low-level, forward-only BSON reader.
/// </summary>
internal sealed class BsonReader : IDisposable
#endif
{
    private const int WindowSize = 8192;

    /// <summary>
    /// Smallest length a JavaScript-with-scope value can declare: its own int32, an empty
    /// string, and an empty document.
    /// </summary>
    private const int JavaScriptWithScopeOverhead = 4 + 5 + BsonSize.DocumentOverhead;

    private readonly Stream? _stream;
    private readonly bool _leaveOpen;
    private readonly bool _canSeek;

    /// <summary>
    /// True when <see cref="_buffer"/> is the caller's own storage rather than a rented read
    /// window: it already holds the whole input, nothing can refill it, and slices of it can
    /// be handed out without copying.
    /// </summary>
    private readonly bool _bufferIsSource;

    // Bytes available to read, as the half-open range [_start, _end) of _buffer. Everything
    // this reader consumes comes from here; the stream is touched only to refill it, which is
    // what keeps an alternative source (ReadOnlySequence, PipeReader) a change to the refill
    // path rather than to every read method.
    private byte[] _buffer;
    private int _start;
    private int _end;

    private readonly Stack<DocumentContext> _contextStack = new();

    // Bytes consumed so far, counted rather than asked of the stream, which a non-seekable one
    // cannot answer. Document ends are tracked against it.
    private long _position;

    // End of the outermost open document, or -1 when none is open. No read crosses it, so a
    // stream holding several documents in sequence stays readable one document at a time.
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
    /// Current element type after calling Read().
    /// </summary>
    public BsonType CurrentType { get; private set; }

    /// <summary>
    /// Current element name after calling Read().
    /// </summary>
    public string CurrentName { get; private set; } = string.Empty;

    /// <summary>
    /// Indicates whether the reader is currently positioned inside an array.
    /// </summary>
    public bool IsInArray => _contextStack.Count > 0 && _contextStack.Peek().IsArray;

    /// <summary>
    /// Reads the start of a document. Must be called before reading elements.
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

        // Forward to the terminator, over any element the caller chose not to read. Stopping
        // one byte short of the end leaves it to be checked below.
        Advance(context.EndPosition - 1 - _position);

        var endMarker = ReadByteCore();
        if (endMarker != 0)
            throw new InvalidDataException($"Expected end of document marker (0x00), got 0x{endMarker:X2}");

        // Out of the outermost document, so the next one gets to declare its own extent.
        if (_contextStack.Count == 0)
            _readLimit = -1;
    }

    /// <summary>
    /// Reads the end of an array. An array is a document on the wire, so this is
    /// <see cref="ReadEndDocument"/> under the name that pairs with
    /// <see cref="ReadStartArray"/> — and with <c>BsonWriter.WriteEndArray</c>.
    /// </summary>
    public void ReadEndArray() => ReadEndDocument();

    /// <summary>
    /// Reads a document's length prefix and opens a context for it.
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
            // Left unchecked, an overlong nested length would let reads and skips inside it
            // run past the enclosing document and misread whatever follows.
            throw new InvalidDataException(
                $"A nested document declares {length} bytes, which does not fit in the document containing it.");
        }

        _contextStack.Push(new DocumentContext { EndPosition = endPosition, IsArray = isArray });
    }

    /// <summary>
    /// Reads the next element header. Returns true if there's an element, false if at end of document.
    /// </summary>
    public bool Read()
    {
        if (_contextStack.Count == 0)
            throw new InvalidOperationException("Not inside a document. Call ReadStartDocument() first.");

        var context = _contextStack.Peek();

        // Check if we're at the end of document
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

    // The numeric readers accept any of the three numeric wire types and convert, so a model
    // whose property widened or narrowed still reads documents written before the change. The
    // switch is the whole type check; a separate EnsureType ahead of it would only repeat the
    // set it already lists.

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
    /// Reads a BSON ObjectId into the provided span.
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
    /// Consumes a binary value's length prefix and subtype byte, leaving the reader on the
    /// payload.
    /// </summary>
    /// <param name="dataLength">
    /// Payload length. The deprecated <see cref="BsonBinarySubType.BinaryOld"/> repeats the
    /// length inside the payload, and it is the inner one that describes the bytes.
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
    /// Reads binary data as a <see cref="ReadOnlyMemory{T}"/>.
    /// When the reader was constructed from a <see cref="byte"/> array or array-backed
    /// <see cref="ReadOnlyMemory{T}"/>, the returned memory is a slice into the source
    /// buffer (no allocation). For stream-based input the data is copied into a new array.
    /// The returned memory aliases the source buffer; mutations to the source are visible.
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
        // The declared length covers the value and its terminator.
        var valueLength = ReadLengthCore(1, "A string") - 1;

        // Decoded straight out of the window when it fits, which is the common case: the
        // alternative copies the bytes into an array only to throw it away.
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
    /// Skips the current element value.
    /// </summary>
    /// <remarks>
    /// Covers every type <see cref="BsonType"/> names, including the deprecated ones this
    /// reader has no accessor for. Generated deserializers skip every field they do not
    /// recognise, so a gap here is a document that cannot be read at all rather than one
    /// field that cannot be.
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
                // No data to skip
                break;
            case BsonType.Regex:
                ReadCString(); // pattern
                ReadCString(); // options
                break;
            case BsonType.DBPointer:
                // Deprecated: a string, then a 12-byte ObjectId.
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
                // Not a type byte this reader knows, so its length is unknowable and the
                // rest of the document would be parsed against the wrong offsets.
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
    /// Reads a null-terminated string. The terminator is normally already in the window, so
    /// the whole name is decoded from one span.
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
            // Nothing can refill a buffer-backed reader, so an absent terminator is the end of
            // the input — unless the window is empty because this reader was disposed.
            ThrowIfDisposed();
            throw new InvalidDataException("Unterminated cstring.");
        }

        return ReadCStringAcrossRefills();
    }

    /// <summary>
    /// The rest of a cstring whose terminator was not in the window, accumulated across
    /// refills.
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
    /// The one place a type mismatch is worded. Takes <see langword="params"/>, which allocates
    /// — on the throwing path only, where the exception costs more than the array.
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

    // Read primitives. Everything this reader consumes goes through one of these so the
    // logical position stays accurate without asking the stream where it is.

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

    // Raw bits keep this little-endian on either endianness, matching BsonWriter.
    private double ReadDoubleCore() => BitConverter.Int64BitsToDouble(ReadInt64Core());

    /// <summary>
    /// Reads a length prefix and rejects one no valid value could carry, so a corrupt length
    /// fails here rather than silently misaligning everything that follows.
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
    /// Fills a caller-owned buffer from the window and then, for anything left, straight from
    /// the stream: a payload larger than the window is never staged through it.
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
    /// Consumes <paramref name="count"/> contiguous bytes from the window. Only for values no
    /// larger than the window; bulk payloads go through <see cref="ReadExact"/>.
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
    /// Rejects use after disposal. Placed on the three methods every read eventually reaches
    /// rather than on each public method: disposal empties the window, so anything that still
    /// has bytes to produce has to refill, copy, or skip to get them. Costs one check per
    /// logical read instead of one per primitive.
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

        // Refills start from the front, so the window can hold `minimum` contiguous bytes
        // however far into it the reader had already got.
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
    /// How full to try to make the window. Read-ahead stops at the end of the outermost open
    /// document, so bytes belonging to whatever follows it on the stream are never consumed —
    /// and a peer that sends one document and then waits is never read into.
    /// </summary>
    private int RefillTarget(int minimum)
    {
        // Nothing open, so nothing bounds a read-ahead: take exactly what was asked for.
        if (_readLimit < 0)
            return minimum;

        var remaining = _readLimit - (_position + _end); // _start is 0 by the time this runs
        var target = _end + (int)Math.Min(remaining, _buffer.Length - _end);
        return target < minimum ? minimum : target;
    }

    /// <summary>
    /// Rejects a read that would run past the outermost open document. Malformed lengths are
    /// the only way to get here, and consuming those bytes would corrupt whatever follows.
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
    /// Moves forward over <paramref name="count"/> bytes without reading them into anything
    /// the caller sees.
    /// </summary>
    private void Advance(long count)
    {
        if (count == 0)
            return;

        ThrowIfDisposed();

        if (count < 0)
        {
            // Only a corrupt length prefix gets here. Returning silently would leave the
            // reader short of where it thinks it is and parse the remainder as garbage.
            throw new InvalidDataException(
                $"A declared length would move the reader {-count} bytes backwards. The input is malformed.");
        }

        EnsureWithinDocument(count);

        // Buffered bytes first: the stream is already past them.
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

        // Nothing to seek with, so the bytes have to be consumed and dropped. The window is
        // empty by now, so it doubles as the discard buffer.
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
            // Cleared: the pool hands this array to the next renter as-is, and it still holds
            // whatever was read through it.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (_stream is null)
            return;

        if (!_leaveOpen)
        {
            _stream.Dispose();
            return;
        }

        // Read-ahead left the stream past what this reader actually consumed. Hand those
        // bytes back, so a caller keeping the stream resumes where the reader stopped.
        //
        // The only place seekability is asked about twice. Everywhere else _canSeek decides
        // alone, but disposal can run after the caller closed the stream underneath us, and
        // rewinding one of those throws — out of a Dispose, over bytes nobody can read anyway.
        if (buffered > 0 && _canSeek && _stream.CanSeek)
            _stream.Position -= buffered;
    }
}
