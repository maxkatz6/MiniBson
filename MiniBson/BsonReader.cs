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
    
    private struct DocumentContext
    {
        public long EndPosition;
        public bool IsArray;
    }

    public BsonReader(byte[] data) : this(new MemoryStream(data), leaveOpen: false)
    {
    }

    public BsonReader(ReadOnlyMemory<byte> data) : this(new MemoryStream(data.ToArray()), leaveOpen: false)
    {
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
        var length = _reader.ReadInt32();
        var endPosition = _stream.Position + length - 4; // -4 because length includes itself
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
        if (_stream.Position < context.EndPosition - 1)
            _stream.Position = context.EndPosition - 1;
        
        var endMarker = _reader.ReadByte();
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
        if (_stream.Position >= context.EndPosition - 1)
        {
            CurrentType = default;
            CurrentName = string.Empty;
            return false;
        }

        CurrentType = (BsonType)_reader.ReadByte();
        
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
        return _reader.ReadByte() != 0;
    }

    /// <summary>
    /// Reads a 32-bit integer value.
    /// </summary>
    public int ReadInt32()
    {
        EnsureType(BsonType.Int32, BsonType.Int64, BsonType.Double);
        return CurrentType switch
        {
            BsonType.Int32 => _reader.ReadInt32(),
            BsonType.Int64 => (int)_reader.ReadInt64(),
            BsonType.Double => (int)_reader.ReadDouble(),
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
            BsonType.Int64 => _reader.ReadInt64(),
            BsonType.Int32 => _reader.ReadInt32(),
            BsonType.Double => (long)_reader.ReadDouble(),
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
            BsonType.Double => _reader.ReadDouble(),
            BsonType.Int32 => _reader.ReadInt32(),
            BsonType.Int64 => _reader.ReadInt64(),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// Reads a string value.
    /// </summary>
    public string ReadString()
    {
        EnsureType(BsonType.String, BsonType.JavaScript, BsonType.Symbol);
        var length = _reader.ReadInt32();
        var bytes = _reader.ReadBytes(length - 1);
        _reader.ReadByte(); // null terminator
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads a DateTime value.
    /// </summary>
    public DateTime ReadDateTime()
    {
        EnsureType(BsonType.DateTime);
        var milliseconds = _reader.ReadInt64();
        return UnixEpoch.AddMilliseconds(milliseconds);
    }

    /// <summary>
    /// Reads a BSON ObjectId (12 bytes).
    /// </summary>
    public byte[] ReadObjectId()
    {
        EnsureType(BsonType.ObjectId);
        return _reader.ReadBytes(12);
    }

    /// <summary>
    /// Reads a BSON ObjectId into the provided span.
    /// </summary>
    public void ReadObjectId(Span<byte> destination)
    {
        EnsureType(BsonType.ObjectId);
        if (destination.Length < 12)
            throw new ArgumentException("Destination must be at least 12 bytes.", nameof(destination));
        
        var bytes = _reader.ReadBytes(12);
        bytes.CopyTo(destination);
    }

    /// <summary>
    /// Reads binary data.
    /// </summary>
    public (byte[] Data, BsonBinarySubType SubType) ReadBinary()
    {
        EnsureType(BsonType.Binary);
        var length = _reader.ReadInt32();
        var subType = (BsonBinarySubType)_reader.ReadByte();
        
        // Handle old binary subtype that has an extra length prefix
        if (subType == BsonBinarySubType.BinaryOld)
        {
            var innerLength = _reader.ReadInt32();
            return (_reader.ReadBytes(innerLength), subType);
        }
        
        return (_reader.ReadBytes(length), subType);
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
        var length = _reader.ReadInt32();
        var bytes = _reader.ReadBytes(length - 1);
        _reader.ReadByte(); // null terminator
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads a BSON timestamp.
    /// </summary>
    public (uint Increment, uint Timestamp) ReadTimestamp()
    {
        EnsureType(BsonType.Timestamp);
        var increment = _reader.ReadUInt32();
        var timestamp = _reader.ReadUInt32();
        return (increment, timestamp);
    }

    /// <summary>
    /// Reads the start of an embedded document.
    /// </summary>
    public void ReadStartNestedDocument()
    {
        EnsureType(BsonType.Document);
        var length = _reader.ReadInt32();
        var endPosition = _stream.Position + length - 4;
        _contextStack.Push(new DocumentContext { EndPosition = endPosition, IsArray = false });
    }

    /// <summary>
    /// Reads the start of an array.
    /// </summary>
    public void ReadStartArray()
    {
        EnsureType(BsonType.Array);
        var length = _reader.ReadInt32();
        var endPosition = _stream.Position + length - 4;
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
                _stream.Position += 8;
                break;
            case BsonType.String:
            case BsonType.JavaScript:
            case BsonType.Symbol:
                var stringLength = _reader.ReadInt32();
                _stream.Position += stringLength;
                break;
            case BsonType.Document:
            case BsonType.Array:
                var docLength = _reader.ReadInt32();
                _stream.Position += docLength - 4;
                break;
            case BsonType.Binary:
                var binLength = _reader.ReadInt32();
                _stream.Position += 1 + binLength; // subtype + data
                break;
            case BsonType.ObjectId:
                _stream.Position += 12;
                break;
            case BsonType.Boolean:
                _stream.Position += 1;
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
                _stream.Position += 4;
                break;
            case BsonType.JavaScriptWithScope:
                var scopeLength = _reader.ReadInt32();
                _stream.Position += scopeLength - 4;
                break;
            case BsonType.Decimal128:
                _stream.Position += 16;
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
        var bytes = new List<byte>();
        byte b;
        while ((b = _reader.ReadByte()) != 0)
        {
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private void EnsureType(params BsonType[] expectedTypes)
    {
        foreach (var expected in expectedTypes)
        {
            if (CurrentType == expected)
                return;
        }
        
        if (expectedTypes.Length == 1)
            throw new InvalidOperationException($"Expected {expectedTypes[0]}, but current type is {CurrentType}.");
        else
            throw new InvalidOperationException($"Expected one of [{string.Join(", ", expectedTypes)}], but current type is {CurrentType}.");
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        _reader.Dispose();
        if (!leaveOpen)
            _stream.Dispose();
    }
}

