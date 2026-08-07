using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Where the fixed staging buffer meets document framing: payloads that bypass the buffer, and
/// length placeholders already flushed by the time the document closes.
/// </summary>
[TestClass]
public sealed class BsonWriterBufferingTests
{
    /// <summary>Matches BsonWriter's staging buffer size.</summary>
    private const int BufferSize = 8192;

    private static byte[] Write(Action<BsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }

        return ms.ToArray();
    }

    private static void AssertLengthPrefixMatches(byte[] document)
    {
        Assert.AreEqual(document.Length, BitConverter.ToInt32(document, 0));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(64)]
    [DataRow(BufferSize - 1)]
    [DataRow(BufferSize)]
    [DataRow(BufferSize + 1)]
    [DataRow(BufferSize * 3)]
    public void BinaryPayloadOfAnySizeRoundTrips(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
            payload[i] = (byte)(i % 251);

        var document = Write(w => w.WriteBinary("data", payload));
        AssertLengthPrefixMatches(document);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("data", reader.CurrentName);

        var (data, subType) = reader.ReadBinary();
        Assert.AreEqual(BsonBinarySubType.Generic, subType);
        CollectionAssert.AreEqual(payload, data);

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    // Crosses the staging boundary through the UTF-8 path rather than the binary one.
    [TestMethod]
    [DataRow(BufferSize - 16)]
    [DataRow(BufferSize + 16)]
    [DataRow(BufferSize * 2)]
    public void LongStringRoundTrips(int length)
    {
        var value = new string('x', length);

        var document = Write(w => w.WriteString("text", value));
        AssertLengthPrefixMatches(document);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(value, reader.ReadString());
        reader.ReadEndDocument();
    }

    // Outgrows the buffer, so the placeholder is already on the stream when the document
    // closes: the seek-back branch of PatchLength.
    [TestMethod]
    public void DocumentLargerThanBufferPatchesLengthAfterFlush()
    {
        const int fieldCount = 2000;

        var document = Write(w =>
        {
            for (var i = 0; i < fieldCount; i++)
                w.WriteInt32("field" + i, i);
        });

        Assert.IsTrue(document.Length > BufferSize, "Test should outgrow the staging buffer.");
        AssertLengthPrefixMatches(document);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();

        for (var i = 0; i < fieldCount; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("field" + i, reader.CurrentName);
            Assert.AreEqual(i, reader.ReadInt32());
        }

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    // Both placeholders flush before either document closes, so two seek-backs have to land
    // on the right offsets.
    [TestMethod]
    public void NestedDocumentsPatchLengthsAfterFlush()
    {
        const int fieldCount = 1500;

        var document = Write(w =>
        {
            w.WriteInt32("before", 1);
            w.WriteStartDocument("inner");
            for (var i = 0; i < fieldCount; i++)
                w.WriteInt32("f" + i, i);
            w.WriteEndDocument();
            w.WriteInt32("after", 2);
        });

        Assert.IsTrue(document.Length > BufferSize, "Test should outgrow the staging buffer.");
        AssertLengthPrefixMatches(document);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("before", reader.CurrentName);
        Assert.AreEqual(1, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("inner", reader.CurrentName);
        reader.ReadStartNestedDocument();
        for (var i = 0; i < fieldCount; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(i, reader.ReadInt32());
        }
        reader.ReadEndDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("after", reader.CurrentName);
        Assert.AreEqual(2, reader.ReadInt32());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    // Closes with WriteEndArray rather than WriteEndDocument, but patches the same way.
    [TestMethod]
    public void LargeArrayPatchesLengthAfterFlush()
    {
        const int count = 3000;

        var document = Write(w =>
        {
            w.WriteStartArray("items");
            for (var i = 0; i < count; i++)
                w.WriteInt32(i);
            w.WriteEndArray();
        });

        Assert.IsTrue(document.Length > BufferSize, "Test should outgrow the staging buffer.");
        AssertLengthPrefixMatches(document);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        reader.ReadStartArray();

        for (var i = 0; i < count; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(i, reader.ReadInt32());
        }

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
        reader.ReadEndDocument();
    }

    // Placeholders must resolve against the writer's starting offset, not against zero.
    [TestMethod]
    public void WriterStartingAtNonZeroOffsetPatchesCorrectly()
    {
        const int prefixLength = 11;
        const int fieldCount = 2000; // Large enough to force the seek-back path.

        using var ms = new MemoryStream();
        ms.Write(new byte[prefixLength], 0, prefixLength);

        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            for (var i = 0; i < fieldCount; i++)
                writer.WriteInt32("field" + i, i);
            writer.WriteEndDocument();
        }

        var all = ms.ToArray();
        var document = all.AsSpan(prefixLength).ToArray();

        Assert.IsTrue(document.Length > BufferSize, "Test should outgrow the staging buffer.");
        AssertLengthPrefixMatches(document);

        // The bytes preceding the writer's origin must be untouched.
        for (var i = 0; i < prefixLength; i++)
            Assert.AreEqual(0, all[i]);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        for (var i = 0; i < fieldCount; i++)
        {
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(i, reader.ReadInt32());
        }
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void SequentialDocumentsEachCarryTheirOwnLength()
    {
        const int documentCount = 40;
        const int fieldCount = 40;

        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            for (var d = 0; d < documentCount; d++)
            {
                writer.WriteStartDocument();
                for (var i = 0; i < fieldCount; i++)
                    writer.WriteInt32("field" + i, d * 1000 + i);
                writer.WriteEndDocument();
            }
        }

        var all = ms.ToArray();
        Assert.IsTrue(all.Length > BufferSize, "Test should span multiple flushes.");

        var offset = 0;
        for (var d = 0; d < documentCount; d++)
        {
            var length = BitConverter.ToInt32(all, offset);
            var document = all.AsSpan(offset, length).ToArray();
            AssertLengthPrefixMatches(document);

            using var reader = new BsonReader(document);
            reader.ReadStartDocument();
            for (var i = 0; i < fieldCount; i++)
            {
                Assert.IsTrue(reader.Read());
                Assert.AreEqual(d * 1000 + i, reader.ReadInt32());
            }
            reader.ReadEndDocument();

            offset += length;
        }

        Assert.AreEqual(all.Length, offset);
    }

    [TestMethod]
    public void DisposeIsIdempotent()
    {
        using var ms = new MemoryStream();
        var writer = new BsonWriter(ms, leaveOpen: true);
        writer.WriteStartDocument();
        writer.WriteInt32("value", 1);
        writer.WriteEndDocument();

        // Returning the pooled buffer twice would corrupt the shared pool.
        writer.Dispose();
        writer.Dispose();

        AssertLengthPrefixMatches(ms.ToArray());
    }

    /// <summary>
    /// Staging is invisible from outside once a document closes. Callers wrote against that
    /// before the buffer existed, and holding a finished document back breaks them silently.
    /// </summary>
    [TestMethod]
    public void ClosingATopLevelDocumentPutsItOnTheStream()
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument();
        writer.WriteInt32("value", 1);
        writer.WriteEndDocument();

        AssertLengthPrefixMatches(ms.ToArray());
    }

    // A document still open is the one case staging is observable, which is what Flush is for.
    [TestMethod]
    public void FlushMakesAnUnfinishedDocumentVisible()
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument();
        writer.WriteInt32("value", 1);

        Assert.AreEqual(0, ms.Length, "An open document stays staged.");

        writer.Flush();
        Assert.AreEqual(15, ms.Length, "Flush should publish the bytes written so far.");
    }

    /// <summary>
    /// A wrapper holding its own buffer is the common shape for a file or a socket, and
    /// nothing reaches the real destination unless the writer flushes it too.
    /// </summary>
    [TestMethod]
    public void FlushReachesThroughABufferedStream()
    {
        using var sink = new MemoryStream();
        using var buffered = new BufferedStream(sink, 4096);

        using (var writer = new BsonWriter(buffered, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteInt32("value", 1);
            writer.WriteEndDocument();

            writer.Flush();
            AssertLengthPrefixMatches(sink.ToArray());
        }

        // And again through disposal, which is the other documented way to publish.
        using (var writer = new BsonWriter(buffered, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteInt32("second", 2);
            writer.WriteEndDocument();
        }

        Assert.AreEqual(33, sink.Length, "Dispose should flush the wrapper as well.");
    }

    /// <summary>
    /// A length the writer will not accept has to be rejected before the element header is
    /// staged, or the document keeps an orphan header naming a value that never arrives.
    /// </summary>
    [TestMethod]
    public void ARejectedLengthWritesNothing()
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument();
        writer.WriteInt32("before", 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteStartArray("items", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteStartDocument("sub", 3));

        writer.WriteInt32("after", 2);
        writer.WriteEndDocument();

        using var reader = new BsonReader(ms.ToArray());
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("before", reader.CurrentName);
        Assert.AreEqual(1, reader.ReadInt32());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("after", reader.CurrentName);
        Assert.AreEqual(2, reader.ReadInt32());
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    // The rejected call must not consume the array index either, or every later key is off.
    [TestMethod]
    public void ARejectedLengthDoesNotConsumeAnArrayIndex()
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument();
        writer.WriteStartArray("items");
        writer.WriteInt32(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteStartNestedArray(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteStartNestedDocument(3));

        writer.WriteInt32(11);
        writer.WriteEndArray();
        writer.WriteEndDocument();

        using var reader = new BsonReader(ms.ToArray());
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        reader.ReadStartArray();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("0", reader.CurrentName);
        Assert.AreEqual(10, reader.ReadInt32());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("1", reader.CurrentName);
        Assert.AreEqual(11, reader.ReadInt32());
        Assert.IsFalse(reader.Read());

        reader.ReadEndDocument();
        reader.ReadEndDocument();
    }

    /// <summary>
    /// A length mismatch is recoverable in the sense that the caller sees an exception rather
    /// than a bad document. What it must not do is leave the enclosing array counting from the
    /// nested array's index.
    /// </summary>
    [TestMethod]
    public void AnArrayLengthMismatchStillRestoresTheEnclosingIndex()
    {
        using var ms = new MemoryStream();
        using var writer = new BsonWriter(ms, leaveOpen: true);

        writer.WriteStartDocument();
        writer.WriteStartArray("items");
        writer.WriteInt32(10);                                    // key "0"

        writer.WriteStartNestedArray(BsonSize.DocumentOverhead + 100); // key "1", wrong length
        writer.WriteInt32(11);
        Assert.Throws<InvalidOperationException>(() => writer.WriteEndArray());

        writer.WriteInt32(12);                                    // must be key "2"
        writer.Flush();

        var bytes = ms.ToArray();
        Assert.IsTrue(Contains(bytes, [0x10, (byte)'2', 0x00]), "The enclosing array should resume at index 2.");
        Assert.IsFalse(Contains(bytes, [0x10, (byte)'1', 0x00]), "Index 1 was the nested array, not an int32.");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }

        return false;
    }

    [TestMethod]
    public void WritingAfterDisposeThrowsObjectDisposed()
    {
        using var ms = new MemoryStream();
        var writer = new BsonWriter(ms, leaveOpen: true);
        writer.WriteStartDocument();
        writer.WriteEndDocument();
        writer.Dispose();

        // The pooled buffer is gone by now, so an unguarded write would fail somewhere inside
        // the staging primitives instead of naming the mistake.
        Assert.Throws<ObjectDisposedException>(() => writer.WriteInt32("value", 1));
        Assert.Throws<ObjectDisposedException>(() => writer.WriteString("name", "value"));
        Assert.Throws<ObjectDisposedException>(() => writer.Flush());
    }
}
