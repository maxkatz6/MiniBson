using System.Text;

namespace MiniBson;

#if MINIBSON_PUBLIC
/// <summary>
/// Encoded sizes of BSON values, in bytes.
/// </summary>
/// <remarks>
/// Each member mirrors the corresponding <see cref="BsonWriter"/> method and must be changed
/// with it.
/// </remarks>
public static class BsonSize
#else
/// <summary>
/// Encoded sizes of BSON values, in bytes.
/// </summary>
/// <remarks>
/// Each member mirrors the corresponding <see cref="BsonWriter"/> method and must be changed
/// with it.
/// </remarks>
internal static class BsonSize
#endif
{
    /// <summary>
    /// Bytes a document costs beyond its elements: the leading int32 length and the trailing null.
    /// </summary>
    public const int DocumentOverhead = 5;

    /// <summary>Size of a boolean value.</summary>
    public const int Boolean = 1;

    /// <summary>Size of an int32 value.</summary>
    public const int Int32 = 4;

    /// <summary>Size of an int64 value.</summary>
    public const int Int64 = 8;

    /// <summary>Size of a double value.</summary>
    public const int Double = 8;

    /// <summary>Size of a DateTime value, stored as int64 milliseconds.</summary>
    public const int DateTime = 8;

    /// <summary>Size of a timestamp value, stored as two uint32s.</summary>
    public const int Timestamp = 8;

    /// <summary>Size of an ObjectId value.</summary>
    public const int ObjectId = 12;

    /// <summary>Size of a GUID: binary length, subtype byte, and 16 payload bytes.</summary>
    public const int Guid = 21;

    /// <summary>
    /// Size of a null, undefined, min-key, or max-key value. These carry no payload.
    /// </summary>
    public const int Empty = 0;

    /// <summary>
    /// Bytes an element costs beyond its value: the type byte and the null-terminated name.
    /// </summary>
    /// <param name="nameByteCount">UTF-8 byte count of the element name, excluding the terminator.</param>
    public static int Element(int nameByteCount) => 1 + nameByteCount + 1;

    /// <summary>
    /// Bytes an element costs beyond its value: the type byte and the null-terminated name.
    /// </summary>
    public static int Element(string name) => Element(Encoding.UTF8.GetByteCount(name));

    /// <summary>
    /// Size of a string value. Returns <see cref="Empty"/> for <see langword="null"/>, matching
    /// a caller that writes <see cref="BsonWriter.WriteNull(string)"/> instead.
    /// </summary>
    public static int String(string? value) =>
        value is null ? Empty : 4 + Encoding.UTF8.GetByteCount(value) + 1;

    /// <summary>Size of a binary value.</summary>
    public static int Binary(int length) => 4 + 1 + length;

    /// <summary>
    /// Size of a binary value using the deprecated <see cref="BsonBinarySubType.BinaryOld"/>
    /// subtype, which repeats the length inside the payload.
    /// </summary>
    public static int BinaryOld(int length) => 4 + 1 + 4 + length;

    /// <summary>Size of a regular expression value.</summary>
    public static int Regex(string pattern, string options) =>
        Encoding.UTF8.GetByteCount(pattern) + 1 + Encoding.UTF8.GetByteCount(options) + 1;

    /// <summary>
    /// Bytes an array costs beyond its element values: the document overhead plus, for each
    /// element, a type byte and the null-terminated decimal index used as its name.
    /// </summary>
    /// <param name="count">Number of elements in the array.</param>
    public static int ArrayOverhead(int count) => DocumentOverhead + count + ArrayKeyBytes(count);

    /// <summary>
    /// Total bytes of the null-terminated decimal keys BSON assigns to array elements,
    /// that is the keys "0" through "<paramref name="count"/> - 1".
    /// </summary>
    /// <remarks>
    /// Computed per digit group, so the cost does not grow with <paramref name="count"/>.
    /// </remarks>
    public static int ArrayKeyBytes(int count)
    {
        if (count <= 0)
            return 0;

        var last = count - 1;
        var total = 0;
        var lower = 0;
        var upper = 9;
        var digits = 1;

        while (lower <= last)
        {
            var high = upper < last ? upper : last;
            total += (high - lower + 1) * (digits + 1);

            if (upper >= last)
                break;

            lower = upper + 1;
            digits++;
            upper = upper > (int.MaxValue - 9) / 10 ? int.MaxValue : upper * 10 + 9;
        }

        return total;
    }
}
