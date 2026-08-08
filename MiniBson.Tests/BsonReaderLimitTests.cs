using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Where the reader stops, and what it does with a length that cannot be correct. In each test
/// here, the reader could give a wrong result in place of an error.
/// </summary>
[TestClass]
public sealed class BsonReaderLimitTests
{
    private static byte[] Write(Action<BsonWriter> write) => BsonTestWriter.Serialize(write);

    /// <summary>The offset of the length prefix of the nested document in <see cref="WithNested"/>.</summary>
    private const int NestedLengthOffset = 4 + 1 + 4; // root length, type byte, "sub\0"

    private static byte[] WithNested() => Write(w =>
    {
        w.Document("sub", d => d.WriteInt32("inner", 1));
        w.WriteInt32("after", 7);
    });

    /// <summary>
    /// A length that is smaller than its own prefix. The subtraction gives a negative distance.
    /// That distance moves the reader backwards, and the reader then uses the wrong offsets for
    /// each byte after this point.
    /// </summary>
    [TestMethod]
    public void SkippingANestedDocumentWithATooShortLengthThrows()
    {
        var document = WithNested();
        BitConverter.GetBytes(2).CopyTo(document, NestedLengthOffset);

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("sub", reader.CurrentName);

        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.Skip());
    }

    [TestMethod]
    public void ReadingANestedDocumentWithATooShortLengthThrows()
    {
        var document = WithNested();
        BitConverter.GetBytes(2).CopyTo(document, NestedLengthOffset);

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.ReadStartNestedDocument());
    }

    /// <summary>
    /// A nested length that goes past its parent. Without this test, a read in that document
    /// goes out of the outer document and consumes the bytes after it.
    /// </summary>
    [TestMethod]
    public void NestedDocumentLongerThanItsParentIsRejected()
    {
        var document = WithNested();
        BitConverter.GetBytes(document.Length + 100).CopyTo(document, NestedLengthOffset);

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.ReadStartNestedDocument());
    }

    [TestMethod]
    public void StringWithATooShortLengthThrows()
    {
        var document = Write(w => w.WriteString("name", "value"));

        // The string's length prefix: root length, type byte, "name\0".
        BitConverter.GetBytes(0).CopyTo(document, 4 + 1 + 5);

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.ReadString());
    }

    /// <summary>
    /// A document that declares more bytes than the input holds fails at the declaration. Without
    /// this test the reader fails later, inside some other value, with a message about that value
    /// and not about the true cause.
    /// </summary>
    [TestMethod]
    public void ADocumentLongerThanTheInputIsRejectedAtTheStart()
    {
        var document = Write(w => w.WriteString("name", "value"));
        BitConverter.GetBytes(document.Length + 100).CopyTo(document, 0);

        var reader = new BsonReader(document);

        var ex = ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.ReadStartDocument());
        StringAssert.Contains(ex.Message, "the input holds");
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

        var reader = new BsonReader(backing.AsMemory(0, truncated));

        // The declared length does not fit in the slice. The reader sees that at the start.
        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.ReadStartDocument());
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

        var reader = new BsonReader(backing.AsMemory(0, document.Length));
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.ReadBinaryMemory(out _));
    }

    /// <summary>
    /// Two documents in one input. A caller slices at
    /// <see cref="BsonReader.BytesConsumed"/> to reach the second one. Thus that value must be
    /// the exact boundary.
    /// </summary>
    [TestMethod]
    public void BytesConsumedLandsOnTheDocumentBoundary()
    {
        var first = Write(w => w.WriteString("doc", "one"));
        var second = Write(w => w.WriteString("doc", "two"));

        var both = new byte[first.Length + second.Length];
        first.CopyTo(both, 0);
        second.CopyTo(both, first.Length);

        var reader = new BsonReader(both);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("one", reader.ReadString());
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();

        Assert.AreEqual(first.Length, reader.BytesConsumed);

        var rest = new BsonReader(both.AsMemory((int)reader.BytesConsumed));
        rest.ReadStartDocument();
        Assert.IsTrue(rest.Read());
        Assert.AreEqual("two", rest.ReadString());
        rest.ReadEndDocument();

        Assert.AreEqual(second.Length, rest.BytesConsumed);
    }

    /// <summary>
    /// The same test, but the first document keeps unread elements. The end of that document must
    /// move the reader to the exact end and no further.
    /// </summary>
    [TestMethod]
    public void EndingADocumentWithUnreadElementsLandsOnTheBoundary()
    {
        var first = Write(w =>
        {
            w.WriteString("doc", "one");
            w.WriteBinary("padding", new byte[100]);
        });
        var second = Write(w => w.WriteString("doc", "two"));

        var both = new byte[first.Length + second.Length];
        first.CopyTo(both, 0);
        second.CopyTo(both, first.Length);

        var reader = new BsonReader(both);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("one", reader.ReadString());
        reader.ReadEndDocument(); // "padding" unread

        Assert.AreEqual(first.Length, reader.BytesConsumed);

        var rest = new BsonReader(both.AsMemory((int)reader.BytesConsumed));
        rest.ReadStartDocument();
        Assert.IsTrue(rest.Read());
        Assert.AreEqual("two", rest.ReadString());
        rest.ReadEndDocument();
    }

    /// <summary>
    /// At the end of the outermost document, the limit goes back to the full input. Without that,
    /// the reader cannot read a second document from the same input.
    /// </summary>
    [TestMethod]
    public void ASecondDocumentIsReadableFromTheSameReader()
    {
        var one = Write(w => w.WriteInt32("n", 1));
        var two = Write(w => w.WriteInt32("n", 2));

        var both = new byte[one.Length + two.Length];
        one.CopyTo(both, 0);
        two.CopyTo(both, one.Length);

        var reader = new BsonReader(both);

        foreach (var expected in new[] { 1, 2 })
        {
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(expected, reader.ReadInt32());
            Assert.IsFalse(reader.Read());
            reader.ReadEndDocument();
        }

        Assert.AreEqual(both.Length, reader.BytesConsumed);
    }

    /// <summary>Long names. They are the values that most often run past a boundary.</summary>
    [TestMethod]
    public void LongFieldNamesReadBack()
    {
        var shortName = new string('a', 200);
        var longName = new string('b', 12_000);

        var document = Write(w =>
        {
            w.WriteInt32(shortName, 1);
            w.WriteInt32(longName, 2);
        });

        var reader = new BsonReader(document);

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
    /// A name with no terminator must give an error. The reader must not make a name from the
    /// bytes after it.
    /// </summary>
    [TestMethod]
    public void AnUnterminatedNameThrows()
    {
        var document = Write(w => w.WriteInt32("name", 1));

        // Overwrite the name's terminator, so the search runs to the end of the document.
        for (var i = 5; i < document.Length; i++)
            if (document[i] == 0)
                document[i] = (byte)'x';

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.Read());
    }

    /// <summary>
    /// <see cref="BsonReader.CurrentName"/> decodes the name when the caller reads the property.
    /// Thus a reader that only skips must still give the correct name on request.
    /// </summary>
    [TestMethod]
    public void CurrentNameSpanMatchesCurrentName()
    {
        var document = Write(w =>
        {
            w.WriteInt32("alpha", 1);
            w.WriteString("beta", "x");
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual("alpha"u8.ToArray(), reader.CurrentNameSpan.ToArray());
        Assert.AreEqual("alpha", reader.CurrentName);
        reader.Skip();

        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual("beta"u8.ToArray(), reader.CurrentNameSpan.ToArray());
        Assert.AreEqual("beta", reader.CurrentName);
        reader.Skip();

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }
}
