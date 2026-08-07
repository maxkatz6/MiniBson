using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Reads over a stream that cannot seek. There the reader finds the end of each document with
/// its own position, and a skip consumes the bytes instead of a seek across them.
/// </summary>
[TestClass]
public sealed class BsonReaderNonSeekableTests
{
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

    /// <summary>
    /// Runs <paramref name="read"/> on the same bytes two times, first with a stream that can
    /// seek and then with a stream that cannot. Thus a difference between the two paths is a
    /// test failure. Without this helper, a test uses only one of the two paths.
    /// </summary>
    private static void ReadBothWays(byte[] document, Action<BsonReader> read, int chunkSize = int.MaxValue)
    {
        using (var seekable = new MemoryStream(document, writable: false))
        using (var reader = new BsonReader(seekable, leaveOpen: true))
            read(reader);

        using (var backing = new MemoryStream(document, writable: false))
        using (var nonSeekable = new NonSeekableStream(backing, chunkSize))
        using (var reader = new BsonReader(nonSeekable, leaveOpen: true))
            read(reader);
    }

    [TestMethod]
    public void ReadsEveryValueTypeWithoutSeeking()
    {
        var objectId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var guid = Guid.NewGuid();
        var when = new DateTime(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var document = Write(w =>
        {
            w.WriteDouble("dbl", 1.5);
            w.WriteString("str", "value");
            w.WriteBoolean("bool", true);
            w.WriteInt32("i32", 42);
            w.WriteInt64("i64", 43L);
            w.WriteDateTime("date", when);
            w.WriteNull("nil");
            w.WriteObjectId("oid", objectId);
            w.WriteGuid("guid", guid);
            w.WriteBinary("bin", [9, 8, 7]);
            w.WriteRegex("re", "^a", "i");
            w.WriteJavaScript("js", "f()");
            w.WriteTimestamp("ts", 1, 2);
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();

            Assert.IsTrue(reader.Read());
            Assert.AreEqual(1.5, reader.ReadDouble());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("value", reader.ReadString());
            Assert.IsTrue(reader.Read());
            Assert.IsTrue(reader.ReadBoolean());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(42, reader.ReadInt32());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(43L, reader.ReadInt64());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(when, reader.ReadDateTime());

            Assert.IsTrue(reader.Read());
            Assert.AreEqual(BsonType.Null, reader.CurrentType);

            Assert.IsTrue(reader.Read());
            CollectionAssert.AreEqual(objectId, reader.ReadObjectId());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(guid, reader.ReadGuid());
            Assert.IsTrue(reader.Read());
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, reader.ReadBinary().Data);

            Assert.IsTrue(reader.Read());
            Assert.AreEqual(("^a", "i"), reader.ReadRegex());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("f()", reader.ReadJavaScript());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual((1u, 2u), reader.ReadTimestamp());

            Assert.IsFalse(reader.Read());
            reader.ReadEndDocument();
        });
    }

    /// <summary>
    /// One byte for each Read call, which a socket can do. Code that expects one call to fill
    /// the request fails here.
    /// </summary>
    [TestMethod]
    public void ReadsCorrectlyWhenTheStreamReturnsOneByteAtATime()
    {
        var document = Write(w =>
        {
            w.WriteString("name", "a longer string value than one byte");
            w.WriteInt64("big", long.MaxValue);
            w.WriteBinary("bin", new byte[64]);
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("a longer string value than one byte", reader.ReadString());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(long.MaxValue, reader.ReadInt64());
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(64, reader.ReadBinary().Data.Length);
            reader.ReadEndDocument();
        }, chunkSize: 1);
    }

    [TestMethod]
    public void SkipsEveryValueTypeWithoutSeeking()
    {
        var document = Write(w =>
        {
            w.WriteDouble("dbl", 1.5);
            w.WriteString("str", "value");
            w.WriteBoolean("bool", true);
            w.WriteInt32("i32", 42);
            w.WriteInt64("i64", 43L);
            w.WriteDateTime("date", DateTime.UtcNow);
            w.WriteNull("nil");
            w.WriteObjectId("oid", new byte[12]);
            w.WriteBinary("bin", [1, 2, 3]);
            w.WriteRegex("re", "^a", "i");
            w.WriteTimestamp("ts", 1, 2);

            w.WriteStartDocument("sub");
            w.WriteInt32("inner", 1);
            w.WriteEndDocument();

            w.WriteStartArray("arr");
            w.WriteInt32(1);
            w.WriteInt32(2);
            w.WriteEndArray();

            w.WriteInt32("last", 99);
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();

            var skipped = 0;
            while (reader.Read())
            {
                if (reader.CurrentName == "last")
                {
                    Assert.AreEqual(99, reader.ReadInt32(), "Skipping left the reader misaligned.");
                    break;
                }

                reader.Skip();
                skipped++;
            }

            Assert.AreEqual(13, skipped);
            reader.ReadEndDocument();
        });
    }

    /// <summary>
    /// A value that is large enough to need more than one buffer when the reader discards it.
    /// </summary>
    [TestMethod]
    public void SkipsBinaryLargerThanTheDiscardBuffer()
    {
        var payload = new byte[40_000];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        var document = Write(w =>
        {
            w.WriteBinary("big", payload);
            w.WriteInt32("after", 7);
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            reader.Skip();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("after", reader.CurrentName);
            Assert.AreEqual(7, reader.ReadInt32());
            reader.ReadEndDocument();
        });
    }

    /// <summary>
    /// The reader leaves a document with unread elements. Before this test, no test used this
    /// branch on the seek path or on the other path.
    /// </summary>
    [TestMethod]
    public void ReadEndDocumentSkipsUnreadFields()
    {
        var document = Write(w =>
        {
            w.WriteInt32("first", 1);
            w.WriteString("second", "ignored");
            w.WriteBinary("third", new byte[128]);
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(1, reader.ReadInt32());

            // Two fields left unread.
            reader.ReadEndDocument();
        });
    }

    [TestMethod]
    public void ReadEndDocumentSkipsUnreadFieldsInNestedDocument()
    {
        var document = Write(w =>
        {
            w.WriteStartDocument("sub");
            w.WriteInt32("a", 1);
            w.WriteString("b", "ignored");
            w.WriteInt32("c", 3);
            w.WriteEndDocument();

            w.WriteInt32("after", 42);
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();

            Assert.IsTrue(reader.Read());
            Assert.AreEqual("sub", reader.CurrentName);
            reader.ReadStartNestedDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(1, reader.ReadInt32());
            reader.ReadEndDocument(); // "b" and "c" unread

            Assert.IsTrue(reader.Read());
            Assert.AreEqual("after", reader.CurrentName);
            Assert.AreEqual(42, reader.ReadInt32());

            reader.ReadEndDocument();
        });
    }

    [TestMethod]
    public void ReadEndDocumentSkipsUnreadArrayElements()
    {
        var document = Write(w =>
        {
            w.WriteStartArray("items");
            for (var i = 0; i < 50; i++)
                w.WriteString("element " + i);
            w.WriteEndArray();

            w.WriteInt32("after", 5);
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();

            Assert.IsTrue(reader.Read());
            reader.ReadStartArray();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("element 0", reader.ReadString());
            reader.ReadEndDocument(); // 49 elements unread

            Assert.IsTrue(reader.Read());
            Assert.AreEqual("after", reader.CurrentName);
            Assert.AreEqual(5, reader.ReadInt32());

            reader.ReadEndDocument();
        });
    }

    /// <summary>
    /// The rule that makes a sequence of documents possible. The reader consumes its own
    /// document and no more bytes. Thus the data after it on the stream stays readable.
    /// </summary>
    [TestMethod]
    public void ConsumesExactlyOneDocumentAndLeavesTheRest()
    {
        var first = Write(w => w.WriteString("doc", "one"));
        var second = Write(w => w.WriteString("doc", "two"));

        var combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);

        using var backing = new MemoryStream(combined, writable: false);
        using var nonSeekable = new NonSeekableStream(backing);
        using var reader = new BsonReader(nonSeekable, leaveOpen: true);

        foreach (var expected in new[] { "one", "two" })
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(expected, reader.ReadString());
            Assert.IsFalse(reader.Read());
            reader.ReadEndDocument();
        }
    }

    /// <summary>
    /// The same test, but the reader leaves the first document early. Thus the skip must stop
    /// at the exact end of that document, and not at some point after the elements that it read.
    /// </summary>
    [TestMethod]
    public void ConsumesExactlyOneDocumentWhenFieldsAreLeftUnread()
    {
        var first = Write(w =>
        {
            w.WriteString("doc", "one");
            w.WriteBinary("padding", new byte[100]);
        });
        var second = Write(w => w.WriteString("doc", "two"));

        var combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);

        using var backing = new MemoryStream(combined, writable: false);
        using var nonSeekable = new NonSeekableStream(backing);
        using var reader = new BsonReader(nonSeekable, leaveOpen: true);

        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("one", reader.ReadString());
        reader.ReadEndDocument(); // "padding" unread

        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("two", reader.ReadString());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void TruncatedDocumentThrows()
    {
        var document = Write(w => w.WriteString("name", "a value long enough to truncate"));
        var truncated = document.AsSpan(0, document.Length - 8).ToArray();

        using var backing = new MemoryStream(truncated, writable: false);
        using var nonSeekable = new NonSeekableStream(backing);
        using var reader = new BsonReader(nonSeekable, leaveOpen: true);

        Assert.Throws<EndOfStreamException>(() =>
        {
            reader.ReadStartDocument();
            while (reader.Read())
                reader.Skip();
            reader.ReadEndDocument();
        });
    }

    [TestMethod]
    public void TruncatedSkipThrows()
    {
        var document = Write(w => w.WriteBinary("bin", new byte[200]));
        var truncated = document.AsSpan(0, document.Length - 50).ToArray();

        using var backing = new MemoryStream(truncated, writable: false);
        using var nonSeekable = new NonSeekableStream(backing);
        using var reader = new BsonReader(nonSeekable, leaveOpen: true);

        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.Throws<EndOfStreamException>(() => reader.Skip());
    }

    [TestMethod]
    public void NestedDocumentsAndArraysReadWithoutSeeking()
    {
        var document = Write(w =>
        {
            w.WriteStartDocument("outer");
            w.WriteStartArray("values");
            w.WriteInt32(1);
            w.WriteStartNestedDocument();
            w.WriteString("deep", "yes");
            w.WriteEndDocument();
            w.WriteEndArray();
            w.WriteEndDocument();
        });

        ReadBothWays(document, reader =>
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            reader.ReadStartNestedDocument();

            Assert.IsTrue(reader.Read());
            reader.ReadStartArray();

            Assert.IsTrue(reader.Read());
            Assert.AreEqual(1, reader.ReadInt32());

            Assert.IsTrue(reader.Read());
            reader.ReadStartNestedDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("yes", reader.ReadString());
            reader.ReadEndDocument();

            Assert.IsFalse(reader.Read());
            reader.ReadEndDocument(); // array
            reader.ReadEndDocument(); // outer
            reader.ReadEndDocument(); // root
        });
    }
}
