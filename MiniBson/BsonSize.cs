using System;
using System.Text;

namespace MiniBson;

/// <summary>
/// The encoded lengths of the BSON values, in bytes.
/// </summary>
/// <remarks>
/// Each member agrees with one <see cref="BsonWriter"/> method. You must change the two types
/// together.
/// </remarks>
#if MINIBSON_PUBLIC
public static class BsonSize
#else
internal static class BsonSize
#endif
{
    /// <summary>
    /// The bytes that a document adds to its elements: the int32 length at the start and the
    /// null byte at the end.
    /// </summary>
    public const int DocumentOverhead = 5;

    /// <summary>The length of a boolean value.</summary>
    public const int Boolean = 1;

    /// <summary>The length of an int32 value.</summary>
    public const int Int32 = 4;

    /// <summary>The length of an int64 value.</summary>
    public const int Int64 = 8;

    /// <summary>The length of a double value.</summary>
    public const int Double = 8;

    /// <summary>The length of a DateTime value, which is an int64 of milliseconds.</summary>
    public const int DateTime = 8;

    /// <summary>The length of a timestamp value, which is two uint32 values.</summary>
    public const int Timestamp = 8;

    /// <summary>The length of an ObjectId value.</summary>
    public const int ObjectId = 12;

    /// <summary>The length of a GUID: the binary length, the subtype byte, and 16 data bytes.</summary>
    public const int Guid = 21;

    /// <summary>
    /// The length of a null, undefined, min-key, or max-key value. These types have no data.
    /// </summary>
    public const int Empty = 0;

    /// <summary>
    /// The bytes that an element adds to its value: the type byte and the name with its null
    /// terminator.
    /// </summary>
    /// <param name="nameByteCount">The UTF-8 byte count of the element name, without the terminator.</param>
    public static int Element(int nameByteCount) => 1 + nameByteCount + 1;

    /// <summary>
    /// The bytes that an element adds to its value: the type byte and the name with its null
    /// terminator.
    /// </summary>
    public static int Element(string name) => Element(Encoding.UTF8.GetByteCount(name));

    /// <summary>
    /// The length of a string value. For <see langword="null"/>, this method returns
    /// <see cref="Empty"/>, which is the length of the value that you must write in its place:
    /// <see cref="BsonWriter.WriteNull(string)"/>.
    /// </summary>
    /// <remarks>
    /// BSON has no string encoding of <see langword="null"/>. Thus
    /// <see cref="BsonWriter.WriteString(string, string)"/> throws an exception for one. If you
    /// measure a null value here and then write it as a string, you measure a document that you
    /// cannot write.
    /// </remarks>
    public static int String(string? value) =>
        value is null ? Empty : 4 + Encoding.UTF8.GetByteCount(value) + 1;

    /// <summary>The length of a binary value.</summary>
    public static int Binary(int length) => 4 + 1 + length;

    /// <summary>
    /// The length of a binary value with the deprecated
    /// <see cref="BsonBinarySubType.BinaryOld"/> subtype. That subtype gives the length a
    /// second time inside the data.
    /// </summary>
    public static int BinaryOld(int length) => 4 + 1 + 4 + length;

    /// <summary>The length of a regular expression value.</summary>
    public static int Regex(string pattern, string options) =>
        Encoding.UTF8.GetByteCount(pattern) + 1 + Encoding.UTF8.GetByteCount(options) + 1;

    /// <summary>
    /// The bytes that an array adds to its element values. These bytes are the document
    /// overhead. For each element, they also include a type byte and the decimal index that
    /// BSON uses as the name, with its null terminator.
    /// </summary>
    /// <param name="count">The number of elements in the array.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is negative, or these bytes alone are more than a BSON length
    /// prefix can express.
    /// </exception>
    public static int ArrayOverhead(int count)
    {
        RequireNonNegative(count);
        return Checked((long)DocumentOverhead + count + ArrayKeyBytesCore(count), count);
    }

    /// <summary>
    /// The total bytes of the decimal keys that BSON gives to the array elements, with their
    /// null terminators. These keys are "0" through "<paramref name="count"/> - 1".
    /// </summary>
    /// <remarks>
    /// This method computes one digit group at a time. Thus the cost does not increase with
    /// <paramref name="count"/>.
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
    /// This total uses 64 bits. An array with keys of ten digits has more key bytes than an int
    /// can hold. A value that wrapped would give an incorrect length.
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
