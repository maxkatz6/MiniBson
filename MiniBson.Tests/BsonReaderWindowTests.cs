using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// The limits of the read window: where it stops, and what it does with a length that cannot be
/// correct. In each test here, the reader could give an incorrect result instead of an error.
/// </summary>
[TestClass]
public sealed class BsonReaderWindowTests
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

    /// <summary>The offset of the length prefix of the nested document in <see cref="WithNested"/>.</summary>
    private const int NestedLengthOffset = 4 + 1 + 4; // root length, type byte, "sub\0"

    private static byte[] WithNested() => Write(w =>
    {
        w.WriteStartDocument("sub");
        w.WriteInt32("inner", 1);
        w.WriteEndDocument();
        w.WriteInt32("after", 7);
    });

    /// <summary>
    /// A length that is smaller than its own prefix. The prefix subtracts to a negative
    /// distance. That distance moves the reader backwards, and the reader then uses the wrong
    /// offsets for each byte after this point.
    /// </summary>
    [TestMethod]
    public void SkippingANestedDocumentWithATooShortLengthThrows()
    {
        var document = WithNested();
        BitConverter.GetBytes(2).CopyTo(document, NestedLengthOffset);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("sub", reader.CurrentName);

        Assert.Throws<InvalidDataException>(() => reader.Skip());
    }

    [TestMethod]
    public void ReadingANestedDocumentWithATooShortLengthThrows()
    {
        var document = WithNested();
        BitConverter.GetBytes(2).CopyTo(document, NestedLengthOffset);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        Assert.Throws<InvalidDataException>(() => reader.ReadStartNestedDocument());
    }

    /// <summary>
    /// A nested length that goes past its parent. Without this test, a read in that document
    /// goes out of the outer document and consumes the bytes after it on the stream.
    /// </summary>
    [TestMethod]
    public void NestedDocumentLongerThanItsParentIsRejected()
    {
        var document = WithNested();
        BitConverter.GetBytes(document.Length + 100).CopyTo(document, NestedLengthOffset);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        Assert.Throws<InvalidDataException>(() => reader.ReadStartNestedDocument());
    }

    [TestMethod]
    public void StringWithATooShortLengthThrows()
    {
        var document = Write(w => w.WriteString("name", "value"));

        // The string's length prefix: root length, type byte, "name\0".
        BitConverter.GetBytes(0).CopyTo(document, 4 + 1 + 5);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        Assert.Throws<InvalidDataException>(() => reader.ReadString());
    }

    /// <summary>
    /// A reader with a slice must not read the bytes behind that slice. Here a short document
    /// comes before a different document. A search for a name terminator past the slice finds
    /// the terminator of the adjacent document. The reader then makes an element name from
    /// bytes that are not part of this document.
    /// </summary>
    [TestMethod]
    public void ReaderOverAMemorySliceStopsAtTheSliceEnd()
    {
        var document = Write(w => w.WriteString("name", "value"));
        var next = Write(w => w.WriteString("later", "other"));

        const int truncated = 4 + 1 + 2; // length prefix, type byte, two bytes of "name"

        var backing = new byte[truncated + next.Length];
        document.AsSpan(0, truncated).CopyTo(backing);
        next.CopyTo(backing, truncated);

        using var reader = new BsonReader(backing.AsMemory(0, truncated));
        reader.ReadStartDocument();

        Assert.Throws<InvalidDataException>(() => reader.Read());
    }

    [TestMethod]
    public void BinaryRunningPastAMemorySliceThrows()
    {
        var document = Write(w => w.WriteBinary("bin", new byte[32]));
        var next = Write(w => w.WriteInt32("later", 1));

        var backing = new byte[document.Length + next.Length];
        document.CopyTo(backing, 0);
        next.CopyTo(backing, document.Length);

        // The binary payload's length prefix, inflated past the end of its own document.
        BitConverter.GetBytes(200).CopyTo(backing, 4 + 1 + 4);

        using var reader = new BsonReader(backing.AsMemory(0, document.Length));
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        Assert.Throws<InvalidDataException>(() => reader.ReadBinaryAsMemory());
    }

    /// <summary>
    /// The read-ahead makes the stream path fast, but it can also consume the next document.
    /// Two readers on one stream that can seek must each get one document.
    /// </summary>
    [TestMethod]
    public void SequentialDocumentsOnASeekableStreamReadOneAtATime()
    {
        var first = Write(w => w.WriteString("doc", "one"));
        var second = Write(w => w.WriteString("doc", "two"));

        using var ms = new MemoryStream();
        ms.Write(first, 0, first.Length);
        ms.Write(second, 0, second.Length);
        ms.Position = 0;

        foreach (var expected in new[] { "one", "two" })
        {
            using var reader = new BsonReader(ms, leaveOpen: true);
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(expected, reader.ReadString());
            Assert.IsFalse(reader.Read());
            reader.ReadEndDocument();
        }

        Assert.AreEqual(ms.Length, ms.Position, "The second reader should have consumed the rest.");
    }

    /// <summary>
    /// The same test, but the reader leaves the first document with unread elements. The skip
    /// must stop at the exact end of that document. Thus it must include the bytes that the
    /// read-ahead already consumed.
    /// </summary>
    [TestMethod]
    public void SkippingToTheEndOfASeekableDocumentLandsOnTheBoundary()
    {
        var first = Write(w =>
        {
            w.WriteString("doc", "one");
            w.WriteBinary("padding", new byte[100]);
        });
        var second = Write(w => w.WriteString("doc", "two"));

        using var ms = new MemoryStream();
        ms.Write(first, 0, first.Length);
        ms.Write(second, 0, second.Length);
        ms.Position = 0;

        using (var reader = new BsonReader(ms, leaveOpen: true))
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("one", reader.ReadString());
            reader.ReadEndDocument(); // "padding" unread
        }

        Assert.AreEqual(first.Length, ms.Position, "Read-ahead should not have run into the second document.");

        using (var reader = new BsonReader(ms, leaveOpen: true))
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("two", reader.ReadString());
            reader.ReadEndDocument();
        }
    }

    /// <summary>
    /// Names that are longer than one refill. The reader finds the terminator on a later
    /// refill, and it must join the parts of the name.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(4096)]
    public void LongFieldNamesSurviveRefills(int chunkSize)
    {
        var shortName = new string('a', 200);
        var longName = new string('b', 12_000);

        var document = Write(w =>
        {
            w.WriteInt32(shortName, 1);
            w.WriteInt32(longName, 2);
        });

        using var backing = new MemoryStream(document, writable: false);
        using var nonSeekable = new NonSeekableStream(backing, chunkSize);
        using var reader = new BsonReader(nonSeekable, leaveOpen: true);

        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(shortName, reader.CurrentName);
        Assert.AreEqual(1, reader.ReadInt32());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(longName, reader.CurrentName);
        Assert.AreEqual(2, reader.ReadInt32());
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// A call to Dispose returns the window to the pool. Without this test, a read after that
    /// call gets the data of the next user of that array. It can also fail with an index error
    /// that shows nothing about the true cause.
    /// </summary>
    [TestMethod]
    public void ReadingAfterDisposeThrowsObjectDisposed()
    {
        var document = Write(w =>
        {
            w.WriteInt32("n", 1);
            w.WriteString("s", "value");
        });

        var reader = new BsonReader(new MemoryStream(document, writable: false));
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    [TestMethod]
    public void SkippingAfterDisposeThrowsObjectDisposed()
    {
        var document = Write(w => w.WriteString("s", new string('x', 64)));

        var reader = new BsonReader(new MemoryStream(document, writable: false));
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Skip());
    }

    /// <summary>A buffer-backed reader rents no window, but you also cannot use it after Dispose.</summary>
    [TestMethod]
    public void ReadingAfterDisposeThrowsForABufferBackedReader()
    {
        var document = Write(w => w.WriteInt32("n", 1));

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }
}
