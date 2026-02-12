using MiniBson;

namespace MiniBson.Tests;

[TestClass]
public sealed class BsonWriterReaderTests
{
    [TestMethod]
    public void WriteAndReadSimpleDocument()
    {
        // Write
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteString("name", "test");
            writer.WriteInt32("value", 42);
            writer.WriteBoolean("flag", true);
            writer.WriteEndDocument();
        }

        // Read
        ms.Position = 0;
        using var reader = new BsonReader(ms);
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
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteStartArray("items");
            writer.WriteInt32(1);
            writer.WriteInt32(2);
            writer.WriteInt32(3);
            writer.WriteEndArray();
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
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
        reader.ReadEndDocument(); // End array
        
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument(); // End root document
    }

    [TestMethod]
    public void WriteAndReadNestedDocument()
    {
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteStartDocument("nested");
            writer.WriteString("inner", "value");
            writer.WriteEndDocument();
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
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

        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteString("str", "hello");
            writer.WriteInt32("i32", -123);
            writer.WriteInt64("i64", 9876543210L);
            writer.WriteDouble("dbl", 3.14159);
            writer.WriteBoolean("bool", false);
            writer.WriteNull("nil");
            writer.WriteDateTime("date", testDate);
            writer.WriteGuid("guid", testGuid);
            writer.WriteBinary("bin", testBinary);
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
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
        var (data, subType) = reader.ReadBinary();
        CollectionAssert.AreEqual(testBinary, data);
        
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void WriteAndReadFromByteArray()
    {
        byte[] bsonData;
        
        using (var ms = new MemoryStream())
        {
            using var writer = new BsonWriter(ms);
            writer.WriteStartDocument();
            writer.WriteString("key", "value");
            writer.WriteEndDocument();
            bsonData = ms.ToArray();
        }

        using var reader = new BsonReader(bsonData);
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
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteString("first", "skip me");
            writer.WriteInt32("second", 42);
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
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
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteString("str", "hello");
            writer.WriteInt32("num", 42);
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
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
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteRegex("pattern", "^test.*$", "im");
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();
        
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("pattern", reader.CurrentName);
        var (pattern, options) = reader.ReadRegex();
        Assert.AreEqual("^test.*$", pattern);
        Assert.AreEqual("im", options);
        
        reader.ReadEndDocument();
    }
}

