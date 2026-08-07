using System;
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
    /// Size of a string value. Returns <see cref="Empty"/> for <see langword="null"/>, which is
    /// the size of the value a caller must write for it: <see cref="BsonWriter.WriteNull(string)"/>.
    /// </summary>
    /// <remarks>
    /// There is no string encoding of <see langword="null"/>, so
    /// <see cref="BsonWriter.WriteString(string, string)"/> throws for one. A caller that
    /// measures a null here and then writes it as a string has measured a document it cannot
    /// produce.
    /// </remarks>
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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is negative, or its framing alone is longer than a BSON length
    /// prefix can express.
    /// </exception>
    public static int ArrayOverhead(int count)
    {
        RequireNonNegative(count);
        return Checked((long)DocumentOverhead + count + ArrayKeyBytesCore(count), count);
    }

    /// <summary>
    /// Total bytes of the null-terminated decimal keys BSON assigns to array elements,
    /// that is the keys "0" through "<paramref name="count"/> - 1".
    /// </summary>
    /// <remarks>
    /// Computed per digit group, so the cost does not grow with <paramref name="count"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is negative, or its keys are longer than a BSON length prefix
    /// can express.
    /// </exception>
    public static int ArrayKeyBytes(int count)
    {
        RequireNonNegative(count);
        return Checked(ArrayKeyBytesCore(count), count);
    }

    /// <summary>
    /// Accumulated in 64 bits: an array long enough to need ten-digit keys costs more key
    /// bytes than an int can hold, and wrapping would hand back a plausible-looking length.
    /// </summary>
    private static long ArrayKeyBytesCore(int count)
    {
        if (count <= 0)
            return 0;

        var last = count - 1;
        var total = 0L;
        var lower = 0;
        var upper = 9;
        var digits = 1;

        while (lower <= last)
        {
            var high = upper < last ? upper : last;
            total += (long)(high - lower + 1) * (digits + 1);

            if (upper >= last)
                break;

            lower = upper + 1;
            digits++;
            upper = upper > (int.MaxValue - 9) / 10 ? int.MaxValue : upper * 10 + 9;
        }

        return total;
    }

    private static void RequireNonNegative(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "An array cannot have a negative element count.");
    }

    private static int Checked(long total, int count)
    {
        if (total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"An array of {count} elements needs {total} bytes of framing, which a BSON " +
                $"length prefix cannot express (the maximum is {int.MaxValue}).");
        }

        return (int)total;
    }
}
