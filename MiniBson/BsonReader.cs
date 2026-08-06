using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniBson;

#if MINIBSON_PUBLIC
/// <summary>
/// A low-level, forward-only BSON reader.
/// </summary>
public sealed class BsonReader(Stream stream, bool leaveOpen = false) : IDisposable
#else
/// <summary>
/// A low-level, forward-only BSON reader.
/// </summary>
internal sealed class BsonReader(Stream stream, bool leaveOpen = false) : IDisposable
#endif
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly BinaryReader _reader = new(stream, Encoding.UTF8, leaveOpen: true);
    private readonly Stack<DocumentContext> _contextStack = new();

    private readonly bool _canSeek = stream.CanSeek;

    // Bytes consumed so far. Used instead of Stream.Position, which a non-seekable stream
    // cannot report. Document ends are tracked against it.
    private long _position;

    // Rented on the first skip over a non-seekable stream, where bytes have to be read and
    // thrown away rather than seeked past.
    private byte[]? _discardBuffer;

    // When the input is backed by a byte[], we keep a direct reference so binary
    // reads can return a ReadOnlyMemory<byte> slice without copying.
    private readonly byte[]? _sourceBuffer;
    private readonly int _sourceOffset;

    private struct DocumentContext
    {
        public long EndPosition;
        public bool IsArray;
    }

    public BsonReader(byte[] data) : this(new MemoryStream(data, writable: false), leaveOpen: false)
    {
        _sourceBuffer = data ?? throw new ArgumentNullException(nameof(data));
        _sourceOffset = 0;
    }

    public BsonReader(ReadOnlyMemory<byte> data)
        : this(CreateMemoryStream(data, out var buffer, out var offset), leaveOpen: false)
    {
        _sourceBuffer = buffer;
        _sourceOffset = offset;
    }

    private static MemoryStream CreateMemoryStream(ReadOnlyMemory<byte> data, out byte[] buffer, out int offset)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            buffer = segment.Array;
            offset = segment.Offset;
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        }

        var copy = data.ToArray();
        buffer = copy;
        offset = 0;
        return new MemoryStream(copy, 0, copy.Length, writable: false);
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
    public void ReadStartDocument()
    {
        var length = ReadInt32Core();
        var endPosition = _position + length - 4; // -4 because length includes itself
        _contextStack.Push(new DocumentContext { EndPosition = endPosition, IsArray = false });
    }

    /// <summary>
    /// Reads the end of a document.
    /// </summary>
    public void ReadEndDocument()
    {
        if (_contextStack.Count == 0)
            throw new InvalidOperationException("No document to end.");
        
        var context = _contextStack.Pop();
        
        // Skip to end position if not already there (handles skipped fields)
        Advance(context.EndPosition - 1 - _position);
        
        var endMarker = ReadByteCore();
        if (endMarker != 0)
            throw new InvalidDataException($"Expected end of document marker (0x00), got 0x{endMarker:X2}");
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

    /// <summary>
    /// Reads a 32-bit integer value.
    /// </summary>
    public int ReadInt32()
    {
        EnsureType(BsonType.Int32, BsonType.Int64, BsonType.Double);
        return CurrentType switch
        {
            BsonType.Int32 => ReadInt32Core(),
            BsonType.Int64 => (int)ReadInt64Core(),
            BsonType.Double => (int)ReadDoubleCore(),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// Reads a 64-bit integer value.
    /// </summary>
    public long ReadInt64()
    {
        EnsureType(BsonType.Int64, BsonType.Int32, BsonType.Double);
        return CurrentType switch
        {
            BsonType.Int64 => ReadInt64Core(),
            BsonType.Int32 => ReadInt32Core(),
            BsonType.Double => (long)ReadDoubleCore(),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// Reads a double value.
    /// </summary>
    public double ReadDouble()
    {
        EnsureType(BsonType.Double, BsonType.Int32, BsonType.Int64);
        return CurrentType switch
        {
            BsonType.Double => ReadDoubleCore(),
            BsonType.Int32 => ReadInt32Core(),
            BsonType.Int64 => ReadInt64Core(),
            _ => throw new InvalidOperationException()
        };
    }

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
        return ReadBytesCore(12);
    }

    /// <summary>
    /// Reads a BSON ObjectId into the provided span.
    /// </summary>
    public void ReadObjectId(Span<byte> destination)
    {
        EnsureType(BsonType.ObjectId);
        if (destination.Length < 12)
            throw new ArgumentException("Destination must be at least 12 bytes.", nameof(destination));

        if (_sourceBuffer is not null)
        {
            var start = _sourceOffset + (int)_position;
            new ReadOnlySpan<byte>(_sourceBuffer, start, 12).CopyTo(destination);
            Advance(12);
            return;
        }

        ReadBytesCore(12).CopyTo(destination);
    }

    /// <summary>
    /// Reads binary data.
    /// </summary>
    public (byte[] Data, BsonBinarySubType SubType) ReadBinary()
    {
        EnsureType(BsonType.Binary);
        var length = ReadInt32Core();
        var subType = (BsonBinarySubType)ReadByteCore();

        // Handle old binary subtype that has an extra length prefix
        if (subType == BsonBinarySubType.BinaryOld)
        {
            var innerLength = ReadInt32Core();
            return (ReadBytesCore(innerLength), subType);
        }

        return (ReadBytesCore(length), subType);
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
        EnsureType(BsonType.Binary);
        var length = ReadInt32Core();
        var subType = (BsonBinarySubType)ReadByteCore();

        var dataLength = length;
        if (subType == BsonBinarySubType.BinaryOld)
        {
            dataLength = ReadInt32Core();
        }

        if (_sourceBuffer is not null)
        {
            var startInBuffer = _sourceOffset + (int)_position;
            var slice = new ReadOnlyMemory<byte>(_sourceBuffer, startInBuffer, dataLength);
            Advance(dataLength);
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
        var length = ReadInt32Core(); // includes null terminator
        if (_sourceBuffer is not null)
        {
            var start = _sourceOffset + (int)_position;
            var valueSpan = new ReadOnlySpan<byte>(_sourceBuffer, start, length - 1);
#if NET6_0_OR_GREATER
            var result = Encoding.UTF8.GetString(valueSpan);
#else
            var result = Encoding.UTF8.GetString(valueSpan.ToArray());
#endif
            Advance(length); // skip value + null terminator
            return result;
        }

        var bytes = ReadBytesCore(length - 1);
        ReadByteCore(); // null terminator
        return Encoding.UTF8.GetString(bytes);
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
        var length = ReadInt32Core();
        var endPosition = _position + length - 4;
        _contextStack.Push(new DocumentContext { EndPosition = endPosition, IsArray = false });
    }

    /// <summary>
    /// Reads the start of an array.
    /// </summary>
    public void ReadStartArray()
    {
        EnsureType(BsonType.Array);
        var length = ReadInt32Core();
        var endPosition = _position + length - 4;
        _contextStack.Push(new DocumentContext { EndPosition = endPosition, IsArray = true });
    }

    /// <summary>
    /// Skips the current element value.
    /// </summary>
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
                var stringLength = ReadInt32Core();
                Advance(stringLength);
                break;
            case BsonType.Document:
            case BsonType.Array:
                var docLength = ReadInt32Core();
                Advance(docLength - 4);
                break;
            case BsonType.Binary:
                var binLength = ReadInt32Core();
                Advance(1 + binLength); // subtype + data
                break;
            case BsonType.ObjectId:
                Advance(12);
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
            case BsonType.Int32:
                Advance(4);
                break;
            case BsonType.JavaScriptWithScope:
                var scopeLength = ReadInt32Core();
                Advance(scopeLength - 4);
                break;
            case BsonType.Decimal128:
                Advance(16);
                break;
            default:
                throw new InvalidDataException($"Unknown BSON type: {CurrentType}");
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

    private string ReadCString()
    {
        if (_sourceBuffer is not null)
        {
            var start = _sourceOffset + (int)_position;
            var span = new ReadOnlySpan<byte>(_sourceBuffer, start, _sourceBuffer.Length - start);
            var nullIdx = span.IndexOf((byte)0);
            if (nullIdx < 0)
                throw new InvalidDataException("Unterminated cstring.");

            var valueSpan = span.Slice(0, nullIdx);
#if NET6_0_OR_GREATER
            var result = Encoding.UTF8.GetString(valueSpan);
#else
            var result = Encoding.UTF8.GetString(valueSpan.ToArray());
#endif
            Advance(nullIdx + 1); // skip past null terminator
            return result;
        }

        return ReadCStringFromStream();
    }

    private void EnsureType(BsonType expected)
    {
        if (CurrentType != expected)
            throw new InvalidOperationException($"Expected {expected}, but current type is {CurrentType}.");
    }

    private void EnsureType(BsonType expected1, BsonType expected2)
    {
        if (CurrentType != expected1 && CurrentType != expected2)
            throw new InvalidOperationException($"Expected one of [{expected1}, {expected2}], but current type is {CurrentType}.");
    }

    private void EnsureType(BsonType expected1, BsonType expected2, BsonType expected3)
    {
        if (CurrentType != expected1 && CurrentType != expected2 && CurrentType != expected3)
            throw new InvalidOperationException($"Expected one of [{expected1}, {expected2}, {expected3}], but current type is {CurrentType}.");
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Read primitives. Everything this reader consumes goes through one of these so the
    // logical position stays accurate without asking the stream where it is.

    private byte ReadByteCore()
    {
        _position += 1;
        return _reader.ReadByte();
    }

    private int ReadInt32Core()
    {
        _position += 4;
        return _reader.ReadInt32();
    }

    private uint ReadUInt32Core()
    {
        _position += 4;
        return _reader.ReadUInt32();
    }

    private long ReadInt64Core()
    {
        _position += 8;
        return _reader.ReadInt64();
    }

    private double ReadDoubleCore()
    {
        _position += 8;
        return _reader.ReadDouble();
    }

    private byte[] ReadBytesCore(int count)
    {
        var bytes = _reader.ReadBytes(count);
        _position += bytes.Length;

        // BinaryReader returns a short array at end of stream rather than throwing, which
        // would otherwise surface as a silently wrong value.
        if (bytes.Length != count)
            throw new EndOfStreamException($"Expected {count} bytes but the input ended after {bytes.Length}.");

        return bytes;
    }

    /// <summary>
    /// Moves forward over <paramref name="count"/> bytes without reading them into anything
    /// the caller sees.
    /// </summary>
    private void Advance(long count)
    {
        if (count <= 0)
            return;

        if (_canSeek)
        {
            _stream.Position += count;
            _position += count;
            return;
        }

        // Nothing to seek with, so the bytes have to be consumed and dropped.
        _discardBuffer ??= ArrayPool<byte>.Shared.Rent(4096);

        while (count > 0)
        {
            var chunk = (int)Math.Min(count, _discardBuffer.Length);
            var read = _stream.Read(_discardBuffer, 0, chunk);
            if (read <= 0)
                throw new EndOfStreamException($"Expected {count} more bytes but the input ended.");

            _position += read;
            count -= read;
        }
    }

    /// <summary>
    /// Reads a cstring one byte at a time. A non-seekable stream cannot be rewound, so the
    /// terminator cannot be found by scanning ahead.
    /// </summary>
    private string ReadCStringFromStream()
    {
        var scratch = ArrayPool<byte>.Shared.Rent(128);
        try
        {
            var length = 0;
            byte b;
            while ((b = ReadByteCore()) != 0)
            {
                if (length == scratch.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(scratch.Length * 2);
                    Buffer.BlockCopy(scratch, 0, larger, 0, length);
                    ArrayPool<byte>.Shared.Return(scratch);
                    scratch = larger;
                }

                scratch[length++] = b;
            }

            return Encoding.UTF8.GetString(scratch, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void Dispose()
    {
        if (_discardBuffer is { } discard)
        {
            _discardBuffer = null;
            ArrayPool<byte>.Shared.Return(discard);
        }

        _reader.Dispose();
        if (!leaveOpen)
            _stream.Dispose();
    }
}

