namespace MiniBson;

#if MINIBSON_PUBLIC
/// <summary>
/// BSON element types as defined in the BSON specification.
/// </summary>
public enum BsonType : byte
#else
/// <summary>
/// BSON element types as defined in the BSON specification.
/// </summary>
internal enum BsonType : byte
#endif
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

#if MINIBSON_PUBLIC
/// <summary>
/// BSON binary subtypes.
/// </summary>
public enum BsonBinarySubType : byte
#else
/// <summary>
/// BSON binary subtypes.
/// </summary>
internal enum BsonBinarySubType : byte
#endif
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
