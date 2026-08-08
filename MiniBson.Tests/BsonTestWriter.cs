using System.Buffers;
using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Writes a document to a buffer and gives back its bytes.
/// </summary>
/// <remarks>
/// <para>
/// Each document needs its length before it starts. A test is about the bytes and not about the
/// arithmetic. Thus these helpers write the body one time to a throwaway writer to learn its
/// length, and then write it a second time for real.
/// <see cref="BsonWriter.WriteEndDocument"/> compares the two. Thus a wrong measurement here
/// fails the test and does not hide in it.
/// </para>
/// </remarks>
internal static class BsonTestWriter
{
    /// <summary>
    /// Writes a top-level document whose elements <paramref name="body"/> writes.
    /// </summary>
    public static byte[] Serialize(Action<BsonWriter> body)
    {
        var bytes = Raw(writer =>
        {
            writer.WriteStartDocument(DocumentLength(body));
            body(writer);
            writer.WriteEndDocument();
        });

        // This test is separate from the test inside the writer. The prefix must equal the number
        // of bytes produced.
        Assert.AreEqual(
            bytes.Length,
            BitConverter.ToInt32(bytes, 0),
            "The document length prefix disagrees with the bytes written.");

        return bytes;
    }

    /// <summary>
    /// Writes whatever <paramref name="write"/> writes, with no framing of its own. Use it when
    /// the test drives the document framing itself.
    /// </summary>
    public static byte[] Raw(Action<BsonWriter> write)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BsonWriter(output);
        write(writer);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The full encoded length of a document whose elements <paramref name="body"/> writes.
    /// </summary>
    /// <remarks>
    /// The probe writer starts its array element counter at zero, and a real document or array
    /// does the same. Thus both passes give the same element names, and the same length.
    /// </remarks>
    public static int DocumentLength(Action<BsonWriter> body) =>
        BsonSize.DocumentOverhead + Raw(body).Length;
}

/// <summary>
/// Framing helpers that measure their own length. They keep a test readable when it puts a
/// document or an array inside another one.
/// </summary>
internal static class BsonWriterTestExtensions
{
    /// <summary>Writes a nested document element.</summary>
    public static void Document(this BsonWriter writer, string name, Action<BsonWriter> body)
    {
        writer.WriteStartDocument(name, BsonTestWriter.DocumentLength(body));
        body(writer);
        writer.WriteEndDocument();
    }

    /// <summary>Writes an array element.</summary>
    public static void Array(this BsonWriter writer, string name, Action<BsonWriter> body)
    {
        writer.WriteStartArray(name, BsonTestWriter.DocumentLength(body));
        body(writer);
        writer.WriteEndArray();
    }

    /// <summary>Writes a nested document as an array element.</summary>
    public static void NestedDocument(this BsonWriter writer, Action<BsonWriter> body)
    {
        writer.WriteStartNestedDocument(BsonTestWriter.DocumentLength(body));
        body(writer);
        writer.WriteEndDocument();
    }

    /// <summary>Writes a nested array as an array element.</summary>
    public static void NestedArray(this BsonWriter writer, Action<BsonWriter> body)
    {
        writer.WriteStartNestedArray(BsonTestWriter.DocumentLength(body));
        body(writer);
        writer.WriteEndArray();
    }
}
