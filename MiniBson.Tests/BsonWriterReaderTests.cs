using System.Runtime.InteropServices;
using MiniBson;

namespace MiniBson.Tests;

[TestClass]
public sealed class BsonWriterReaderTests
{
    [TestMethod]
    public void WriteAndReadSimpleDocument()
    {
        var document = BsonTestWriter.Serialize(writer =>
        {
            writer.WriteString("name", "test");
            writer.WriteInt32("value", 42);
            writer.WriteBoolean("flag", true);
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("name", reader.CurrentName);
        Assert.AreEqual(BsonType.String, reader.CurrentType);
        Assert.AreEqual("test", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("value", reader.CurrentName);
        Assert.AreEqual(BsonType.Int32, reader.CurrentType);
        Assert.AreEqual(42, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("flag", reader.CurrentName);
        Assert.AreEqual(BsonType.Boolean, reader.CurrentType);
        Assert.IsTrue(reader.ReadBoolean());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void WriteAndReadArray()
    {
        var document = BsonTestWriter.Serialize(writer =>
        {
            writer.Array("items", a =>
            {
                a.WriteInt32(1);
                a.WriteInt32(2);
                a.WriteInt32(3);
            });
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("items", reader.CurrentName);
        Assert.AreEqual(BsonType.Array, reader.CurrentType);

        reader.ReadStartArray();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(3, reader.ReadInt32());

        Assert.IsFalse(reader.Read());
        reader.ReadEndArray();

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// The reader ends an array and a document with the same operation. The API gives it two
    /// names, so the read code can agree with the write code that made the bytes.
    /// </summary>
    [TestMethod]
    public void ReadEndArrayAndReadEndDocumentAreInterchangeable()
    {
        var document = BsonTestWriter.Serialize(writer =>
        {
            writer.Array("items", a => a.WriteInt32(1));
        });

        static int ReadWith(byte[] document, Action<BsonReader> endArray)
        {
            var reader = new BsonReader(document);
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            reader.ReadStartArray();
            Assert.IsTrue(reader.Read());
            var value = reader.ReadInt32();
            Assert.IsFalse(reader.Read());
            endArray(reader);
            Assert.IsFalse(reader.Read());
            reader.ReadEndDocument();
            return value;
        }

        Assert.AreEqual(1, ReadWith(document, r => r.ReadEndArray()));
        Assert.AreEqual(1, ReadWith(document, r => r.ReadEndDocument()));
    }

    [TestMethod]
    public void WriteAndReadNestedDocument()
    {
        var document = BsonTestWriter.Serialize(writer =>
        {
            writer.Document("nested", d => d.WriteString("inner", "value"));
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("nested", reader.CurrentName);
        Assert.AreEqual(BsonType.Document, reader.CurrentType);

        reader.ReadStartNestedDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("inner", reader.CurrentName);
        Assert.AreEqual("value", reader.ReadString());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void WriteAndReadAllTypes()
    {
        var testDate = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var testGuid = Guid.NewGuid();
        var testBinary = new byte[] { 1, 2, 3, 4, 5 };

        var document = BsonTestWriter.Serialize(writer =>
        {
            writer.WriteString("str", "hello");
            writer.WriteInt32("i32", -123);
            writer.WriteInt64("i64", 9876543210L);
            writer.WriteDouble("dbl", 3.14159);
            writer.WriteBoolean("bool", false);
            writer.WriteNull("nil");
            writer.WriteDateTime("date", testDate);
            writer.WriteGuid("guid", testGuid);
            writer.WriteBinary("bin", testBinary);
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("hello", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(-123, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(9876543210L, reader.ReadInt64());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(3.14159, reader.ReadDouble(), 0.00001);

        Assert.IsTrue(reader.Read());
        Assert.IsFalse(reader.ReadBoolean());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(BsonType.Null, reader.CurrentType);

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(testDate, reader.ReadDateTime());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(testGuid, reader.ReadGuid());

        Assert.IsTrue(reader.Read());
        var data = reader.ReadBinaryArray(out _);
        CollectionAssert.AreEqual(testBinary, data);

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// A reader over caller memory gives back a slice of it and copies nothing.
    /// </summary>
    [TestMethod]
    public void ReadBinaryMemoryFromAByteArraySlicesIntoTheSource()
    {
        var testBinary = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        var document = BsonTestWriter.Serialize(writer => writer.WriteBinary("bin", testBinary));

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        var data = reader.ReadBinaryMemory(out _);

        CollectionAssert.AreEqual(testBinary, data.ToArray());
        Assert.IsTrue(MemoryMarshal.TryGetArray(data, out var segment));
        Assert.AreSame(document, segment.Array, "Binary memory should alias the source byte[] (zero-copy).");

        reader.ReadEndDocument();
    }

    /// <summary>
    /// The window is the caller's slice and not the whole array, so the offset has to survive.
    /// </summary>
    [TestMethod]
    public void ReadBinaryMemoryFromReadOnlyMemorySlicesIntoTheSource()
    {
        var testBinary = new byte[] { 1, 2, 3, 4, 5 };
        var document = BsonTestWriter.Serialize(writer => writer.WriteBinary("bin", testBinary));

        // Wrap with an offset to verify the offset is respected.
        var padded = new byte[document.Length + 8];
        Buffer.BlockCopy(document, 0, padded, 4, document.Length);
        var input = new ReadOnlyMemory<byte>(padded, 4, document.Length);

        var reader = new BsonReader(input);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        var data = reader.ReadBinaryMemory(out _);

        CollectionAssert.AreEqual(testBinary, data.ToArray());
        Assert.IsTrue(MemoryMarshal.TryGetArray(data, out var segment));
        Assert.AreSame(padded, segment.Array, "Binary memory should alias the padded source array.");

        reader.ReadEndDocument();
    }

    [TestMethod]
    public void ReadBinaryAlwaysCopies()
    {
        var testBinary = new byte[] { 9, 8, 7 };
        var document = BsonTestWriter.Serialize(writer => writer.WriteBinary("bin", testBinary));

        var reader = new BsonReader(document);
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        var data = reader.ReadBinaryArray(out var subType);

        CollectionAssert.AreEqual(testBinary, data);
        Assert.AreEqual(BsonBinarySubType.Generic, subType);
        Assert.AreNotSame(document, data);

        reader.ReadEndDocument();
    }

    [TestMethod]
    public void WriteAndReadFromByteArray()
    {
        var document = BsonTestWriter.Serialize(writer => writer.WriteString("key", "value"));

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("key", reader.CurrentName);
        Assert.AreEqual("value", reader.ReadString());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void SkipElement()
    {
        var document = BsonTestWriter.Serialize(writer =>
        {
            writer.WriteString("first", "skip me");
            writer.WriteInt32("second", 42);
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("first", reader.CurrentName);
        reader.Skip(); // Skip the string value

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("second", reader.CurrentName);
        Assert.AreEqual(42, reader.ReadInt32());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void ReadValueAsDynamic()
    {
        var document = BsonTestWriter.Serialize(writer =>
        {
            writer.WriteString("str", "hello");
            writer.WriteInt32("num", 42);
        });

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("hello", reader.ReadValue());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.ReadValue());

        reader.ReadEndDocument();
    }

    [TestMethod]
    public void WriteRegex()
    {
        var document = BsonTestWriter.Serialize(writer => writer.WriteRegex("pattern", "^test.*$", "im"));

        var reader = new BsonReader(document);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("pattern", reader.CurrentName);
        var (pattern, options) = reader.ReadRegex();
        Assert.AreEqual("^test.*$", pattern);
        Assert.AreEqual("im", options);

        reader.ReadEndDocument();
    }
}
