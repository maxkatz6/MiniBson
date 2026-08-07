using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// The bounded read window: what it refuses to read past, and what it does with a declared
/// length that cannot be true. Every case here is one the reader could answer with plausible
/// nonsense instead of an error.
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

    /// <summary>Offset of the nested document's own length prefix in <see cref="WithNested"/>.</summary>
    private const int NestedLengthOffset = 4 + 1 + 4; // root length, type byte, "sub\0"

    private static byte[] WithNested() => Write(w =>
    {
        w.WriteStartDocument("sub");
        w.WriteInt32("inner", 1);
        w.WriteEndDocument();
        w.WriteInt32("after", 7);
    });

    /// <summary>
    /// A length shorter than the prefix that declares it. Subtracting the prefix gives a
    /// negative distance, and moving the reader backwards by it leaves everything after this
    /// point parsed against the wrong offsets.
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
    /// A nested length that overruns its parent. Reads inside it would otherwise walk out of
    /// the enclosing document and consume whatever followed on the stream.
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
    /// A reader given a slice must not look at the bytes behind it. With a truncated document
    /// followed by an unrelated one, scanning for a name terminator past the slice finds the
    /// neighbour's and composes a field name from bytes that were never part of this document.
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
    /// Read-ahead is what makes the stream path cheap, and it is also the thing that could
    /// swallow the next document. Two readers over one seekable stream have to see one
    /// document each.
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
    /// Same, but the first document is abandoned with fields unread. The skip has to land on
    /// the boundary exactly, which means accounting for what read-ahead already consumed.
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
    /// Names longer than a single refill, so the terminator is found on a later one and the
    /// pieces have to be stitched back together.
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
}
