using System.Buffers;
using System.Text;
using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// The writer against destinations that hand out very little memory at a time.
/// </summary>
/// <remarks>
/// The writer asks for adjacent bytes only for a scalar or the digits of an array index. That is
/// twelve bytes at the most. Each longer value fills a buffer, commits it, asks for another one,
/// and repeats. These tests hold that rule. They run the same documents through a destination
/// that gives one, two, three, or seven bytes at a time.
/// </remarks>
[TestClass]
public sealed class BsonWriterOutputTests
{
    /// <summary>One byte at a time is the least that a destination is allowed to give.</summary>
    private static IEnumerable<object[]> SegmentSizes =>
    [
        [1], [2], [3], [7], [64], [8192]
    ];

    private static byte[] WriteThrough(int segmentSize, Action<BsonWriter> write)
    {
        var output = new SegmentedBufferWriter(segmentSize);
        var writer = new BsonWriter(output);
        write(writer);
        writer.Flush();
        return output.WrittenBytes;
    }

    /// <summary>
    /// The same document through a segmented destination and through a normal one must give the
    /// same bytes.
    /// </summary>
    private static byte[] AssertSameAsArrayBufferWriter(int segmentSize, Action<BsonWriter> write)
    {
        var segmented = WriteThrough(segmentSize, write);
        var contiguous = BsonTestWriter.Raw(write);

        CollectionAssert.AreEqual(
            contiguous,
            segmented,
            $"A destination handing out {segmentSize} bytes at a time produced different bytes.");

        return segmented;
    }

    [TestMethod]
    [DynamicData(nameof(SegmentSizes))]
    public void ScalarsSurviveASegmentedDestination(int segmentSize)
    {
        var document = AssertSameAsArrayBufferWriter(segmentSize, w =>
        {
            var length = BsonSize.DocumentOverhead
                + BsonSize.Element("i32") + BsonSize.Int32
                + BsonSize.Element("i64") + BsonSize.Int64
                + BsonSize.Element("dbl") + BsonSize.Double
                + BsonSize.Element("bool") + BsonSize.Boolean
                + BsonSize.Element("nil")
                + BsonSize.Element("ts") + BsonSize.Timestamp
                + BsonSize.Element("oid") + BsonSize.ObjectId;

            w.WriteStartDocument(length);
            w.WriteInt32("i32", int.MinValue);
            w.WriteInt64("i64", long.MaxValue);
            w.WriteDouble("dbl", Math.PI);
            w.WriteBoolean("bool", true);
            w.WriteNull("nil");
            w.WriteTimestamp("ts", 7, 9);
            w.WriteObjectId("oid", new byte[12]);
            w.WriteEndDocument();
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(int.MinValue, reader.ReadInt32());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(long.MaxValue, reader.ReadInt64());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(Math.PI, reader.ReadDouble(), 0.0);
        Assert.IsTrue(reader.Read());
        Assert.IsTrue(reader.ReadBoolean());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(BsonType.Null, reader.CurrentType);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual((7u, 9u), reader.ReadTimestamp());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(12, reader.ReadObjectId().Length);
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// Values near and past the size of one buffer. A payload longer than one buffer must go
    /// across several buffers.
    /// </summary>
    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(1, 1)]
    [DataRow(1, 64)]
    [DataRow(2, 8191)]
    [DataRow(3, 8192)]
    [DataRow(7, 8193)]
    [DataRow(64, 24576)]
    [DataRow(8192, 24576)]
    public void BinaryOfEverySizeSurvivesASegmentedDestination(int segmentSize, int payloadSize)
    {
        var payload = new byte[payloadSize];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        var document = AssertSameAsArrayBufferWriter(segmentSize, w =>
        {
            w.WriteStartDocument(
                BsonSize.DocumentOverhead + BsonSize.Element("bin") + BsonSize.Binary(payload.Length));
            w.WriteBinary("bin", payload);
            w.WriteEndDocument();
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual(payload, reader.ReadBinaryArray(out _));
        reader.ReadEndDocument();
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(1, 255)]
    [DataRow(2, 256)]
    [DataRow(3, 257)]
    [DataRow(7, 12000)]
    [DataRow(8192, 12000)]
    public void LongStringsAndNamesSurviveASegmentedDestination(int segmentSize, int length)
    {
        var value = new string('x', length);
        var name = new string('n', length);

        var document = AssertSameAsArrayBufferWriter(segmentSize, w =>
        {
            w.WriteStartDocument(
                BsonSize.DocumentOverhead + BsonSize.Element(name) + BsonSize.String(value));
            w.WriteString(name, value);
            w.WriteEndDocument();
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(name, reader.CurrentName);
        Assert.AreEqual(value, reader.ReadString());
        reader.ReadEndDocument();
    }

    /// <summary>A split inside multi-byte UTF-8 must not change the bytes.</summary>
    [TestMethod]
    [DynamicData(nameof(SegmentSizes))]
    public void NonAsciiTextSurvivesASegmentedDestination(int segmentSize)
    {
        const string value = "αβγ 漢字 🎉 déjà vu";

        var document = AssertSameAsArrayBufferWriter(segmentSize, w =>
        {
            w.WriteStartDocument(
                BsonSize.DocumentOverhead + BsonSize.Element("t") + BsonSize.String(value));
            w.WriteString("t", value);
            w.WriteEndDocument();
        });

        Assert.AreEqual(Encoding.UTF8.GetByteCount(value) + 1, BitConverter.ToInt32(document, 7));

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(value, reader.ReadString());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// The writer puts array keys into the destination as digits. Thus a key that crosses a
    /// buffer boundary is the case to test. This one covers each digit count up to four.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(7)]
    public void ArrayKeysAcrossDigitWidthsSurviveASegmentedDestination(int segmentSize)
    {
        const int count = 1005;

        var arrayLength = BsonSize.ArrayOverhead(count) + count * BsonSize.Int32;

        var document = AssertSameAsArrayBufferWriter(segmentSize, w =>
        {
            w.WriteStartDocument(BsonSize.DocumentOverhead + BsonSize.Element("items") + arrayLength);
            w.WriteStartArray("items", arrayLength);
            for (var i = 0; i < count; i++)
                w.WriteInt32(i);
            w.WriteEndArray();
            w.WriteEndDocument();
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        reader.ReadStartArray();
        for (var i = 0; i < count; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(i.ToString(), reader.CurrentName);
            Assert.AreEqual(i, reader.ReadInt32());
        }
        reader.ReadEndDocument();
        reader.ReadEndDocument();
    }

    /// <summary>
    /// The end of the outermost document commits each byte. Thus a caller that reads the
    /// destination sees a complete document and makes no call of its own.
    /// </summary>
    [TestMethod]
    public void ClosingTheTopLevelDocumentCommitsIt()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BsonWriter(output);

        writer.WriteStartDocument(BsonSize.DocumentOverhead + BsonSize.Element("n") + BsonSize.Int32);
        writer.WriteInt32("n", 1);

        Assert.AreEqual(0, output.WrittenCount, "Nothing should be committed before the document closes.");

        writer.WriteEndDocument();

        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("n") + BsonSize.Int32,
            output.WrittenCount);
    }

    /// <summary>Sequential documents through one writer land back to back.</summary>
    [TestMethod]
    public void SequentialDocumentsAppend()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BsonWriter(output);

        var one = BsonSize.DocumentOverhead + BsonSize.Element("a") + BsonSize.Int32;

        for (var i = 0; i < 3; i++)
        {
            writer.WriteStartDocument(one);
            writer.WriteInt32("a", i);
            writer.WriteEndDocument();
        }

        Assert.AreEqual(one * 3, output.WrittenCount);

        var all = output.WrittenSpan.ToArray();
        for (var i = 0; i < 3; i++)
        {
            var reader = new BsonReader(new ReadOnlyMemory<byte>(all, i * one, one));
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(i, reader.ReadInt32());
            reader.ReadEndDocument();
        }
    }

    /// <summary>A caller needs Flush for a document that it does not finish.</summary>
    [TestMethod]
    public void FlushCommitsAnIncompleteDocument()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new BsonWriter(output);

        writer.WriteStartDocument(1024);
        writer.WriteInt32("n", 1);

        Assert.AreEqual(0, output.WrittenCount);

        writer.Flush();

        Assert.AreEqual(4 + BsonSize.Element("n") + BsonSize.Int32, output.WrittenCount);
    }

    [TestMethod]
    public void BytesWrittenCountsBufferedAndCommittedBytes()
    {
        var writer = new BsonWriter(new ArrayBufferWriter<byte>());
        Assert.AreEqual(0L, writer.BytesWritten);

        var length = BsonSize.DocumentOverhead + BsonSize.Element("n") + BsonSize.Int32;
        writer.WriteStartDocument(length);
        Assert.AreEqual(4L, writer.BytesWritten);

        writer.WriteInt32("n", 1);
        writer.WriteEndDocument();
        Assert.AreEqual((long)length, writer.BytesWritten);
    }

    /// <summary>
    /// A destination that breaks the contract must give a clear message. It must not fail at some
    /// other place inside the writer.
    /// </summary>
    [TestMethod]
    public void ADestinationThatUnderdeliversThrows()
    {
        var writer = new BsonWriter(new StingyBufferWriter());

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => writer.WriteStartDocument(BsonSize.DocumentOverhead));

        StringAssert.Contains(ex.Message, "IBufferWriter");
    }

    /// <summary>A destination that ignores the size hint and gives one byte each time.</summary>
    private sealed class StingyBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _one = new byte[1];

        public void Advance(int count) { }

        public Memory<byte> GetMemory(int sizeHint = 0) => _one;

        public Span<byte> GetSpan(int sizeHint = 0) => _one;
    }
}
