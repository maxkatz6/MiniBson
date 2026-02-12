using System.IO;
using System.Text;

namespace MiniBson;

/// <summary>
/// BSON element types as defined in the BSON specification.
/// </summary>
public enum BsonType : byte
{
    Double = 0x01,
    String = 0x02,
    Document = 0x03,
    Array = 0x04,
    Binary = 0x05,
    Undefined = 0x06, // Deprecated
    ObjectId = 0x07,
    Boolean = 0x08,
    DateTime = 0x09,
    Null = 0x0A,
    Regex = 0x0B,
    DBPointer = 0x0C, // Deprecated
    JavaScript = 0x0D,
    Symbol = 0x0E, // Deprecated
    JavaScriptWithScope = 0x0F, // Deprecated
    Int32 = 0x10,
    Timestamp = 0x11,
    Int64 = 0x12,
    Decimal128 = 0x13,
    MinKey = 0xFF,
    MaxKey = 0x7F,
}

/// <summary>
/// BSON binary subtypes.
/// </summary>
public enum BsonBinarySubType : byte
{
    Generic = 0x00,
    Function = 0x01,
    BinaryOld = 0x02, // Deprecated
    UuidOld = 0x03, // Deprecated
    Uuid = 0x04,
    Md5 = 0x05,
    Encrypted = 0x06,
    CompressedTimeSeries = 0x07,
    UserDefined = 0x80,
}

/// <summary>
/// A low-level, forward-only BSON writer.
/// </summary>
public sealed class BsonWriter(Stream stream, bool leaveOpen = false) : IDisposable
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly BinaryWriter _writer = new(stream, Encoding.UTF8, leaveOpen: true);
    private readonly Stack<long> _documentStartPositions = new();
    private int _arrayIndex;
    private readonly Stack<int> _arrayIndexStack = new();

    /// <summary>
    /// Writes the start of a BSON document.
    /// </summary>
    public void WriteStartDocument()
    {
        _documentStartPositions.Push(_stream.Position);
        _writer.Write(0); // Placeholder for document length
    }

    /// <summary>
    /// Writes the end of a BSON document.
    /// </summary>
    public void WriteEndDocument()
    {
        _writer.Write((byte)0); // End of document marker
        
        var endPosition = _stream.Position;
        var startPosition = _documentStartPositions.Pop();
        var length = (int)(endPosition - startPosition);
        
        _stream.Position = startPosition;
        _writer.Write(length);
        _stream.Position = endPosition;
    }

    /// <summary>
    /// Writes the start of a BSON array.
    /// </summary>
    public void WriteStartArray(string name)
    {
        WriteType(BsonType.Array);
        WriteCString(name);
        _arrayIndexStack.Push(_arrayIndex);
        _arrayIndex = 0;
        WriteStartDocument();
    }

    /// <summary>
    /// Writes the end of a BSON array.
    /// </summary>
    public void WriteEndArray()
    {
        WriteEndDocument();
        _arrayIndex = _arrayIndexStack.Pop();
    }

    /// <summary>
    /// Writes a nested document field.
    /// </summary>
    public void WriteStartDocument(string name)
    {
        WriteType(BsonType.Document);
        WriteCString(name);
        WriteStartDocument();
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
        _writer.Write(value ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Writes a 32-bit integer value.
    /// </summary>
    public void WriteInt32(string name, int value)
    {
        WriteType(BsonType.Int32);
        WriteCString(name);
        _writer.Write(value);
    }

    /// <summary>
    /// Writes a 64-bit integer value.
    /// </summary>
    public void WriteInt64(string name, long value)
    {
        WriteType(BsonType.Int64);
        WriteCString(name);
        _writer.Write(value);
    }

    /// <summary>
    /// Writes a double-precision floating point value.
    /// </summary>
    public void WriteDouble(string name, double value)
    {
        WriteType(BsonType.Double);
        WriteCString(name);
        _writer.Write(value);
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
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        var milliseconds = (long)(utc - UnixEpoch).TotalMilliseconds;
        _writer.Write(milliseconds);
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
#if NET6_0_OR_GREATER
        _writer.Write(value);
#else
        _writer.Write(value.ToArray());
#endif
    }

    /// <summary>
    /// Writes binary data.
    /// </summary>
    public void WriteBinary(string name, ReadOnlySpan<byte> value, BsonBinarySubType subType = BsonBinarySubType.Generic)
    {
        WriteType(BsonType.Binary);
        WriteCString(name);
        _writer.Write(value.Length);
        _writer.Write((byte)subType);
#if NET6_0_OR_GREATER
        _writer.Write(value);
#else
        _writer.Write(value.ToArray());
#endif
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
        _writer.Write(increment);
        _writer.Write(timestamp);
    }

    // Array element writers (without name - uses index)
    
    /// <summary>
    /// Writes a null array element.
    /// </summary>
    public void WriteNull()
    {
        WriteType(BsonType.Null);
        WriteCString(_arrayIndex++.ToString());
    }

    /// <summary>
    /// Writes a boolean array element.
    /// </summary>
    public void WriteBoolean(bool value)
    {
        WriteBoolean(_arrayIndex++.ToString(), value);
    }

    /// <summary>
    /// Writes an int32 array element.
    /// </summary>
    public void WriteInt32(int value)
    {
        WriteInt32(_arrayIndex++.ToString(), value);
    }

    /// <summary>
    /// Writes an int64 array element.
    /// </summary>
    public void WriteInt64(long value)
    {
        WriteInt64(_arrayIndex++.ToString(), value);
    }

    /// <summary>
    /// Writes a double array element.
    /// </summary>
    public void WriteDouble(double value)
    {
        WriteDouble(_arrayIndex++.ToString(), value);
    }

    /// <summary>
    /// Writes a string array element.
    /// </summary>
    public void WriteString(string value)
    {
        WriteString(_arrayIndex++.ToString(), value);
    }

    /// <summary>
    /// Writes a DateTime array element.
    /// </summary>
    public void WriteDateTime(DateTime value)
    {
        WriteDateTime(_arrayIndex++.ToString(), value);
    }

    /// <summary>
    /// Writes a nested document array element.
    /// </summary>
    public void WriteStartNestedDocument()
    {
        WriteStartDocument(_arrayIndex++.ToString());
    }

    /// <summary>
    /// Writes a nested array element.
    /// </summary>
    public void WriteStartNestedArray()
    {
        WriteStartArray(_arrayIndex++.ToString());
    }

    private void WriteType(BsonType type)
    {
        _writer.Write((byte)type);
    }

    private void WriteCString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        _writer.Write(bytes);
        _writer.Write((byte)0);
    }

    private void WriteStringValue(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        _writer.Write(bytes.Length + 1); // Length includes null terminator
        _writer.Write(bytes);
        _writer.Write((byte)0);
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        _writer.Dispose();
        if (!leaveOpen)
            _stream.Dispose();
    }
}


