using System.Text;
using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Skipping types the reader has no accessor for. Generated deserializers skip every field
/// they do not recognise, so a type missing from <see cref="BsonReader.Skip"/> is not one
/// unreadable field — it is a document that cannot be read past that point at all.
/// </summary>
/// <remarks>
/// The documents here are assembled byte by byte, because <see cref="BsonWriter"/> cannot
/// write the deprecated types this covers.
/// </remarks>
[TestClass]
public sealed class BsonReaderSkipTests
{
    private static readonly byte[] SampleObjectId =
        [0x50, 0x7F, 0x1F, 0x77, 0xBC, 0xF8, 0x6C, 0xD7, 0x99, 0x43, 0x90, 0x11];

    private static byte[] Document(params byte[][] elements)
    {
        var body = elements.SelectMany(e => e).ToArray();
        var document = new byte[4 + body.Length + 1];
        BitConverter.GetBytes(document.Length).CopyTo(document, 0);
        body.CopyTo(document, 4);
        return document; // the trailing terminator is already zero
    }

    private static byte[] CString(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

    /// <summary>A length-prefixed string; the declared length counts the terminator.</summary>
    private static byte[] String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [.. BitConverter.GetBytes(bytes.Length + 1), .. bytes, 0];
    }

    private static byte[] Element(BsonType type, string name) => [(byte)type, .. CString(name)];

    private static byte[] Int32Element(string name, int value) =>
        [.. Element(BsonType.Int32, name), .. BitConverter.GetBytes(value)];

    /// <summary>A namespace string followed by a 12-byte ObjectId.</summary>
    private static byte[] DbPointer(string name, string collection) =>
        [.. Element(BsonType.DBPointer, name), .. String(collection), .. SampleObjectId];

    private static byte[] MinKey(string name) => Element(BsonType.MinKey, name);

    private static byte[] Decimal128(string name) =>
        [.. Element(BsonType.Decimal128, name), .. new byte[16]];

    /// <summary>
    /// Reads the element after a skipped one, on a seekable stream and a non-seekable one.
    /// Skipping seeks where it can and consumes bytes where it cannot, so a wrong length is
    /// visible on only one of the two.
    /// </summary>
    private static void AssertSkipsTo(byte[] document, string expectedName, int expectedValue)
    {
        var (seekable, streamed) = DualPathReader.Read(document, reader =>
        {
            reader.ReadStartDocument();

            Assert.IsTrue(reader.Read());
            reader.Skip();

            Assert.IsTrue(reader.Read());
            Assert.AreEqual(expectedName, reader.CurrentName);
            var value = reader.ReadInt32();

            Assert.IsFalse(reader.Read());
            reader.ReadEndDocument();
            return value;
        });

        Assert.AreEqual(expectedValue, seekable);
        Assert.AreEqual(expectedValue, streamed);
    }

    [TestMethod]
    public void SkipsADbPointer() =>
        AssertSkipsTo(
            Document(DbPointer("ptr", "some.collection"), Int32Element("after", 42)),
            "after",
            42);

    [TestMethod]
    public void SkipsAMinKey() =>
        AssertSkipsTo(Document(MinKey("min"), Int32Element("after", 7)), "after", 7);

    [TestMethod]
    public void SkipsADecimal128() =>
        AssertSkipsTo(Document(Decimal128("money"), Int32Element("after", 9)), "after", 9);

    [TestMethod]
    public void DbPointerIsReportedAsItsOwnType()
    {
        var document = Document(DbPointer("ptr", "some.collection"));

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("ptr", reader.CurrentName);
        Assert.AreEqual(BsonType.DBPointer, reader.CurrentType);
    }

    /// <summary>
    /// A byte that names no type has no length either, so skipping it would parse everything
    /// after it against the wrong offsets.
    /// </summary>
    [TestMethod]
    public void AnUnknownTypeByteIsRejected()
    {
        const byte unassigned = 0x7E;
        var document = Document([unassigned, .. CString("weird")]);

        using var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("weird", reader.CurrentName);

        var exception = Assert.Throws<InvalidDataException>(() => reader.Skip());
        StringAssert.Contains(exception.Message, "0x7E");
    }
}
