using System.Buffers;
using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Each document carries a length that the caller computed. The writer cannot go back and write a
/// length later. Thus the test in <see cref="BsonWriter.WriteEndDocument"/>, which compares that
/// length against the bytes written, is the one test that finds a document with a wrong size.
/// </summary>
[TestClass]
public sealed class BsonWriterKnownLengthTests
{
    private static readonly byte[] SamplePayload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private const string SampleText = "hello";

    private static readonly int SampleNestedLength =
        BsonSize.DocumentOverhead + BsonSize.Element("b") + BsonSize.Boolean;

    private static readonly int SampleArrayLength =
        BsonSize.ArrayOverhead(3) + 3 * BsonSize.Int32;

    private static readonly int SampleLength =
        BsonSize.DocumentOverhead
        + BsonSize.Element("n") + BsonSize.Int32
        + BsonSize.Element("s") + BsonSize.String(SampleText)
        + BsonSize.Element("arr") + SampleArrayLength
        + BsonSize.Element("sub") + SampleNestedLength
        + BsonSize.Element("bin") + BsonSize.Binary(SamplePayload.Length);

    /// <summary>
    /// One document with scalars, a string, an array, a nested document, and binary data. Each
    /// length comes from <see cref="BsonSize"/>.
    /// </summary>
    private static void WriteSample(BsonWriter w)
    {
        w.WriteStartDocument(SampleLength);

        w.WriteInt32("n", 42);
        w.WriteString("s", SampleText);

        w.WriteStartArray("arr", SampleArrayLength);
        w.WriteInt32(1);
        w.WriteInt32(2);
        w.WriteInt32(3);
        w.WriteEndArray();

        w.WriteStartDocument("sub", SampleNestedLength);
        w.WriteBoolean("b", true);
        w.WriteEndDocument();

        w.WriteBinary("bin", SamplePayload);

        w.WriteEndDocument();
    }

    private static void AssertSampleReadsBack(byte[] document)
    {
        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("n", reader.CurrentName);
        Assert.AreEqual(42, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("s", reader.CurrentName);
        Assert.AreEqual(SampleText, reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("arr", reader.CurrentName);
        reader.ReadStartArray();
        for (var i = 1; i <= 3; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(i, reader.ReadInt32());
        }
        reader.ReadEndDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("sub", reader.CurrentName);
        reader.ReadStartNestedDocument();
        Assert.IsTrue(reader.Read());
        Assert.IsTrue(reader.ReadBoolean());
        reader.ReadEndDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("bin", reader.CurrentName);
        CollectionAssert.AreEqual(SamplePayload, reader.ReadBinaryArray(out _));

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// Each <see cref="BsonSize"/> helper against the bytes that the writer produced. The writer
    /// throws on a disagreement, so this test also fixes the total.
    /// </summary>
    [TestMethod]
    public void SizeHelpersAgreeWithTheBytesWritten()
    {
        var document = BsonTestWriter.Raw(WriteSample);

        Assert.AreEqual(SampleLength, document.Length, "BsonSize disagrees with what the writer emitted.");
        AssertSampleReadsBack(document);
    }

    /// <summary>
    /// Longer than one buffer from the destination. Thus the document goes across several
    /// buffers, and no one buffer holds all of it.
    /// </summary>
    [TestMethod]
    public void LargeSizedDocumentWrites()
    {
        const int fieldCount = 4000;

        var length = BsonSize.DocumentOverhead;
        for (var i = 0; i < fieldCount; i++)
            length += BsonSize.Element("field" + i) + BsonSize.Int32;

        var document = BsonTestWriter.Raw(writer =>
        {
            writer.WriteStartDocument(length);
            for (var i = 0; i < fieldCount; i++)
                writer.WriteInt32("field" + i, i);
            writer.WriteEndDocument();
        });

        Assert.AreEqual(length, document.Length);
        Assert.IsTrue(document.Length > 8192, "Test should span more than one destination buffer.");

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        for (var i = 0; i < fieldCount; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("field" + i, reader.CurrentName);
            Assert.AreEqual(i, reader.ReadInt32());
        }
        reader.ReadEndDocument();
    }

    /// <summary>Zero is not a placeholder for an unknown length. It is a length that is too small.</summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(-4)]
    [DataRow(BsonSize.DocumentOverhead - 1)]
    public void ImplausibleLengthThrows(int documentLength)
    {
        var writer = new BsonWriter(new ArrayBufferWriter<byte>());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.WriteStartDocument(documentLength));
    }

    /// <summary>A rejected length must leave the destination untouched.</summary>
    [TestMethod]
    public void ARejectedLengthWritesNothing()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BsonWriter(output);

        writer.WriteStartDocument(BsonSize.DocumentOverhead);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.WriteStartDocument("sub", 3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.WriteStartArray("arr", 3));

        writer.WriteEndDocument();

        Assert.AreEqual(BsonSize.DocumentOverhead, output.WrittenCount,
            "A rejected length left an element header behind.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(-1)]
    public void LengthMismatchThrowsAtEndOfDocument(int delta)
    {
        var declared = BsonSize.DocumentOverhead + BsonSize.Element("n") + BsonSize.Int32 + delta;

        var writer = new BsonWriter(new ArrayBufferWriter<byte>());

        writer.WriteStartDocument(declared);
        writer.WriteInt32("n", 1);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => writer.WriteEndDocument());
        StringAssert.Contains(ex.Message, declared.ToString());
    }

    [TestMethod]
    public void NestedLengthMismatchThrowsAtEndOfNestedDocument()
    {
        var writer = new BsonWriter(new ArrayBufferWriter<byte>());

        writer.WriteStartDocument(BsonSize.DocumentOverhead + BsonSize.Element("sub") + SampleNestedLength);
        writer.WriteStartDocument("sub", SampleNestedLength + 4);
        writer.WriteBoolean("b", true);

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.WriteEndDocument());
    }

    [TestMethod]
    public void ArrayLengthMismatchThrowsAtEndOfArray()
    {
        var writer = new BsonWriter(new ArrayBufferWriter<byte>());

        writer.WriteStartDocument(BsonSize.DocumentOverhead + BsonSize.Element("arr") + SampleArrayLength);
        writer.WriteStartArray("arr", SampleArrayLength);
        writer.WriteInt32(1);
        writer.WriteInt32(2); // One element short of the declared length.

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.WriteEndArray());
    }

    /// <summary>
    /// The length test runs before the element header. Thus a rejected nested array must not use
    /// an array index. Without that order, a caller that catches the exception writes the rest of
    /// the array under the wrong names.
    /// </summary>
    [TestMethod]
    public void ARejectedNestedLengthDoesNotConsumeAnArrayIndex()
    {
        var elementLength = BsonSize.DocumentOverhead + BsonSize.Element("v") + BsonSize.Int32;
        var arrayLength = BsonSize.ArrayOverhead(2) + BsonSize.Int32 + elementLength;

        var document = BsonTestWriter.Raw(writer =>
        {
            writer.WriteStartDocument(BsonSize.DocumentOverhead + BsonSize.Element("items") + arrayLength);
            writer.WriteStartArray("items", arrayLength);

            writer.WriteInt32(7); // key "0"

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.WriteStartNestedArray(3));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.WriteStartNestedDocument(3));

            writer.WriteStartNestedDocument(elementLength); // must still be key "1"
            writer.WriteInt32("v", 9);
            writer.WriteEndDocument();

            writer.WriteEndArray();
            writer.WriteEndDocument();
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        reader.ReadStartArray();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("0", reader.CurrentName);
        Assert.AreEqual(7, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("1", reader.CurrentName, "The rejected element consumed an array index.");
        reader.ReadStartNestedDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(9, reader.ReadInt32());
        reader.ReadEndDocument();

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
        reader.ReadEndDocument();
    }

    /// <summary>
    /// The counter of the outer array goes back even when the length test throws. Thus a caller
    /// that catches the exception continues at the correct number.
    /// </summary>
    [TestMethod]
    public void AFailedNestedArrayRestoresTheOuterArrayIndex()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BsonWriter(output);

        writer.WriteStartDocument(1024);
        writer.WriteStartArray("items", 512);
        writer.WriteInt32(1); // key "0"

        writer.WriteStartNestedArray(BsonSize.DocumentOverhead + 100); // key "1", wrong length
        Assert.ThrowsExactly<InvalidOperationException>(() => writer.WriteEndArray());

        // Commit the bytes so far. Then write one more element of the outer array and read the
        // key that the writer gave it.
        writer.Flush();
        var before = output.WrittenCount;

        writer.WriteNull();
        writer.Flush();

        var element = output.WrittenSpan.Slice(before).ToArray();
        Assert.AreEqual((byte)BsonType.Null, element[0]);
        Assert.AreEqual((byte)'2', element[1], "The outer array's element counter was not restored.");
        Assert.AreEqual(0, element[2]);
    }

    [TestMethod]
    public void SizedNestedDocumentsInsideArrays()
    {
        var elementLength = BsonSize.DocumentOverhead + BsonSize.Element("v") + BsonSize.Int32;
        var arrayLength = BsonSize.ArrayOverhead(2) + 2 * elementLength;
        var totalLength = BsonSize.DocumentOverhead + BsonSize.Element("items") + arrayLength;

        var document = BsonTestWriter.Raw(writer =>
        {
            writer.WriteStartDocument(totalLength);
            writer.WriteStartArray("items", arrayLength);
            for (var i = 0; i < 2; i++)
            {
                writer.WriteStartNestedDocument(elementLength);
                writer.WriteInt32("v", i);
                writer.WriteEndDocument();
            }
            writer.WriteEndArray();
            writer.WriteEndDocument();
        });

        Assert.AreEqual(totalLength, document.Length);

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        reader.ReadStartArray();
        for (var i = 0; i < 2; i++)
        {
            Assert.IsTrue(reader.Read());
            reader.ReadStartNestedDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(i, reader.ReadInt32());
            reader.ReadEndDocument();
        }
        reader.ReadEndDocument();
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void EndingADocumentThatWasNeverStartedThrows()
    {
        var writer = new BsonWriter(new ArrayBufferWriter<byte>());

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.WriteEndDocument());
    }
}
