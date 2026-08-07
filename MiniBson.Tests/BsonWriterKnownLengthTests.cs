using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Documents whose length is supplied up front rather than patched in afterwards, which is
/// what lets one be written to a stream that cannot be seeked.
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
    /// One document covering scalars, a string, an array, a nested document, and binary. With
    /// <paramref name="sized"/> set, every length is supplied up front.
    /// </summary>
    private static void WriteSample(BsonWriter w, bool sized)
    {
        w.WriteStartDocument(sized ? SampleLength : 0);

        w.WriteInt32("n", 42);
        w.WriteString("s", SampleText);

        w.WriteStartArray("arr", sized ? SampleArrayLength : 0);
        w.WriteInt32(1);
        w.WriteInt32(2);
        w.WriteInt32(3);
        w.WriteEndArray();

        w.WriteStartDocument("sub", sized ? SampleNestedLength : 0);
        w.WriteBoolean("b", true);
        w.WriteEndDocument();

        w.WriteBinary("bin", SamplePayload);

        w.WriteEndDocument();
    }

    private static void AssertSampleReadsBack(byte[] document)
    {
        using var reader = new BsonReader(document);
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
        CollectionAssert.AreEqual(SamplePayload, reader.ReadBinary().Data);

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    // The strongest available check: supplying lengths must not change a single output byte.
    [TestMethod]
    public void SizedAndPatchedOutputAreByteIdentical()
    {
        using var patchedStream = new MemoryStream();
        using (var writer = new BsonWriter(patchedStream, leaveOpen: true))
            WriteSample(writer, sized: false);

        using var sizedStream = new MemoryStream();
        using (var writer = new BsonWriter(sizedStream, leaveOpen: true))
            WriteSample(writer, sized: true);

        var patched = patchedStream.ToArray();
        var sized = sizedStream.ToArray();

        CollectionAssert.AreEqual(patched, sized);
        Assert.AreEqual(SampleLength, patched.Length, "BsonSize disagrees with what the writer emitted.");
        AssertSampleReadsBack(sized);
    }

    [TestMethod]
    public void SizedDocumentWritesToNonSeekableStream()
    {
        using var backing = new MemoryStream();
        using (var nonSeekable = new NonSeekableStream(backing))
        using (var writer = new BsonWriter(nonSeekable, leaveOpen: true))
        {
            Assert.IsTrue(writer.RequiresKnownLength);
            WriteSample(writer, sized: true);
        }

        var document = backing.ToArray();
        Assert.AreEqual(SampleLength, document.Length);
        AssertSampleReadsBack(document);
    }

    // Larger than the staging buffer, so there is no in-buffer placeholder to fall back on:
    // confirms the sized path never reaches for the stream position.
    [TestMethod]
    public void LargeSizedDocumentWritesToNonSeekableStream()
    {
        const int fieldCount = 4000;

        var length = BsonSize.DocumentOverhead;
        for (var i = 0; i < fieldCount; i++)
            length += BsonSize.Element("field" + i) + BsonSize.Int32;

        using var backing = new MemoryStream();
        using (var nonSeekable = new NonSeekableStream(backing))
        using (var writer = new BsonWriter(nonSeekable, leaveOpen: true))
        {
            writer.WriteStartDocument(length);
            for (var i = 0; i < fieldCount; i++)
                writer.WriteInt32("field" + i, i);
            writer.WriteEndDocument();
        }

        var document = backing.ToArray();
        Assert.AreEqual(length, document.Length);
        Assert.IsTrue(document.Length > 8192, "Test should outgrow the staging buffer.");

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        for (var i = 0; i < fieldCount; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("field" + i, reader.CurrentName);
            Assert.AreEqual(i, reader.ReadInt32());
        }
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void RequiresKnownLengthReflectsTheStream()
    {
        using var seekable = new MemoryStream();
        using (var writer = new BsonWriter(seekable, leaveOpen: true))
            Assert.IsFalse(writer.RequiresKnownLength);

        using var backing = new MemoryStream();
        using var nonSeekable = new NonSeekableStream(backing);
        using (var writer = new BsonWriter(nonSeekable, leaveOpen: true))
            Assert.IsTrue(writer.RequiresKnownLength);
    }

    [TestMethod]
    public void UnsizedDocumentOnNonSeekableStreamThrows()
    {
        using var backing = new MemoryStream();
        using var nonSeekable = new NonSeekableStream(backing);
        using var writer = new BsonWriter(nonSeekable, leaveOpen: true);

        var ex = Assert.Throws<InvalidOperationException>(() => writer.WriteStartDocument());
        StringAssert.Contains(ex.Message, "WriteStartDocument");
    }

    // Must surface when the nested document opens, not as corrupt output.
    [TestMethod]
    public void UnsizedNestedDocumentOnNonSeekableStreamThrows()
    {
        using var backing = new MemoryStream();
        using var nonSeekable = new NonSeekableStream(backing);
        using var writer = new BsonWriter(nonSeekable, leaveOpen: true);

        writer.WriteStartDocument(BsonSize.DocumentOverhead);
        Assert.Throws<InvalidOperationException>(() => writer.WriteStartDocument("sub"));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(-4)]
    [DataRow(BsonSize.DocumentOverhead - 1)]
    public void ImplausibleLengthThrows(int documentLength)
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteStartDocument(documentLength));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(-1)]
    public void LengthMismatchThrowsAtEndOfDocument(int delta)
    {
        var declared = BsonSize.DocumentOverhead + BsonSize.Element("n") + BsonSize.Int32 + delta;

        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument(declared);
        writer.WriteInt32("n", 1);

        var ex = Assert.Throws<InvalidOperationException>(() => writer.WriteEndDocument());
        StringAssert.Contains(ex.Message, declared.ToString());
    }

    [TestMethod]
    public void NestedLengthMismatchThrowsAtEndOfNestedDocument()
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument();
        writer.WriteStartDocument("sub", SampleNestedLength + 4);
        writer.WriteBoolean("b", true);

        Assert.Throws<InvalidOperationException>(() => writer.WriteEndDocument());
    }

    [TestMethod]
    public void ArrayLengthMismatchThrowsAtEndOfArray()
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument();
        writer.WriteStartArray("arr", SampleArrayLength);
        writer.WriteInt32(1);
        writer.WriteInt32(2); // One element short of the declared length.

        Assert.Throws<InvalidOperationException>(() => writer.WriteEndArray());
    }

    [TestMethod]
    public void SizedNestedDocumentsInsideArrays()
    {
        var elementLength = BsonSize.DocumentOverhead + BsonSize.Element("v") + BsonSize.Int32;
        var arrayLength = BsonSize.ArrayOverhead(2) + 2 * elementLength;
        var totalLength = BsonSize.DocumentOverhead + BsonSize.Element("items") + arrayLength;

        using var backing = new MemoryStream();
        using (var nonSeekable = new NonSeekableStream(backing))
        using (var writer = new BsonWriter(nonSeekable, leaveOpen: true))
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
        }

        var document = backing.ToArray();
        Assert.AreEqual(totalLength, document.Length);

        using var reader = new BsonReader(document);
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
}
