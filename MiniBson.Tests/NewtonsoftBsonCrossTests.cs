using MiniBson;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;

namespace MiniBson.Tests;

[TestClass]
public sealed class NewtonsoftBsonCrossTests
{
    /// <summary>
    /// Tests that MiniBson can read BSON written by Newtonsoft.Json.Bson
    /// </summary>
    [TestMethod]
    public void ReadNewtonsoftWrittenSimpleDocument()
    {
        // Write with Newtonsoft
        using var ms = new MemoryStream();
        using (var writer = new BsonDataWriter(ms) { CloseOutput = false })
        {
            writer.WriteStartObject();
            writer.WritePropertyName("name");
            writer.WriteValue("John");
            writer.WritePropertyName("age");
            writer.WriteValue(30);
            writer.WritePropertyName("active");
            writer.WriteValue(true);
            writer.WriteEndObject();
        }

        // Read with MiniBson
        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("name", reader.CurrentName);
        Assert.AreEqual("John", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("age", reader.CurrentName);
        Assert.AreEqual(30, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("active", reader.CurrentName);
        Assert.IsTrue(reader.ReadBoolean());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// Tests that Newtonsoft can read BSON written by MiniBson
    /// </summary>
    [TestMethod]
    public void NewtonsoftReadsMiniBsonWrittenDocument()
    {
        // Write with MiniBson
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteString("city", "Seattle");
            writer.WriteInt32("population", 750000);
            writer.WriteDouble("latitude", 47.6062);
            writer.WriteEndDocument();
        }

        // Read with Newtonsoft
        ms.Position = 0;
        using var reader = new BsonDataReader(ms);
        
        Assert.IsTrue(reader.Read()); // StartObject
        Assert.AreEqual(JsonToken.StartObject, reader.TokenType);

        Assert.IsTrue(reader.Read()); // PropertyName: city
        Assert.AreEqual(JsonToken.PropertyName, reader.TokenType);
        Assert.AreEqual("city", reader.Value);

        Assert.IsTrue(reader.Read()); // Value: Seattle
        Assert.AreEqual(JsonToken.String, reader.TokenType);
        Assert.AreEqual("Seattle", reader.Value);

        Assert.IsTrue(reader.Read()); // PropertyName: population
        Assert.AreEqual(JsonToken.PropertyName, reader.TokenType);
        Assert.AreEqual("population", reader.Value);

        Assert.IsTrue(reader.Read()); // Value: 750000
        Assert.AreEqual(JsonToken.Integer, reader.TokenType);
        Assert.AreEqual(750000L, reader.Value);

        Assert.IsTrue(reader.Read()); // PropertyName: latitude
        Assert.AreEqual(JsonToken.PropertyName, reader.TokenType);
        Assert.AreEqual("latitude", reader.Value);

        Assert.IsTrue(reader.Read()); // Value: 47.6062
        Assert.AreEqual(JsonToken.Float, reader.TokenType);
        Assert.AreEqual(47.6062, reader.Value);

        Assert.IsTrue(reader.Read()); // EndObject
        Assert.AreEqual(JsonToken.EndObject, reader.TokenType);
    }

    /// <summary>
    /// Tests arrays written by Newtonsoft, read by MiniBson
    /// </summary>
    [TestMethod]
    public void ReadNewtonsoftWrittenArray()
    {
        using var ms = new MemoryStream();
        using (var writer = new BsonDataWriter(ms) { CloseOutput = false })
        {
            writer.WriteStartObject();
            writer.WritePropertyName("numbers");
            writer.WriteStartArray();
            writer.WriteValue(10);
            writer.WriteValue(20);
            writer.WriteValue(30);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("numbers", reader.CurrentName);
        Assert.AreEqual(BsonType.Array, reader.CurrentType);

        reader.ReadStartArray();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(10, reader.ReadInt64()); // Newtonsoft writes integers as Int64

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(20, reader.ReadInt64());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(30, reader.ReadInt64());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();

        reader.ReadEndDocument();
    }

    /// <summary>
    /// Tests arrays written by MiniBson, read by Newtonsoft
    /// </summary>
    [TestMethod]
    public void NewtonsoftReadsMiniBsonWrittenArray()
    {
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteStartArray("items");
            writer.WriteString("apple");
            writer.WriteString("banana");
            writer.WriteString("cherry");
            writer.WriteEndArray();
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        using var reader = new BsonDataReader(ms);
        
        Assert.IsTrue(reader.Read()); // StartObject
        Assert.IsTrue(reader.Read()); // PropertyName: items
        Assert.AreEqual("items", reader.Value);
        
        Assert.IsTrue(reader.Read()); // StartArray
        Assert.AreEqual(JsonToken.StartArray, reader.TokenType);

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("apple", reader.Value);

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("banana", reader.Value);

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("cherry", reader.Value);

        Assert.IsTrue(reader.Read()); // EndArray
        Assert.AreEqual(JsonToken.EndArray, reader.TokenType);

        Assert.IsTrue(reader.Read()); // EndObject
        Assert.AreEqual(JsonToken.EndObject, reader.TokenType);
    }

    /// <summary>
    /// Tests nested documents cross-compatibility
    /// </summary>
    [TestMethod]
    public void ReadNewtonsoftWrittenNestedDocument()
    {
        using var ms = new MemoryStream();
        using (var writer = new BsonDataWriter(ms) { CloseOutput = false })
        {
            writer.WriteStartObject();
            writer.WritePropertyName("person");
            writer.WriteStartObject();
            writer.WritePropertyName("firstName");
            writer.WriteValue("Alice");
            writer.WritePropertyName("lastName");
            writer.WriteValue("Smith");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("person", reader.CurrentName);
        Assert.AreEqual(BsonType.Document, reader.CurrentType);

        reader.ReadStartNestedDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("firstName", reader.CurrentName);
        Assert.AreEqual("Alice", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("lastName", reader.CurrentName);
        Assert.AreEqual("Smith", reader.ReadString());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    /// <summary>
    /// Tests DateTime compatibility between MiniBson and Newtonsoft
    /// </summary>
    [TestMethod]
    public void DateTimeRoundTrip()
    {
        var testDate = new DateTime(2024, 6, 15, 14, 30, 45, DateTimeKind.Utc);

        // Write with MiniBson
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteDateTime("timestamp", testDate);
            writer.WriteEndDocument();
        }

        // Read with Newtonsoft
        ms.Position = 0;
        using var newtonsoftReader = new BsonDataReader(ms);
        newtonsoftReader.ReadRootValueAsArray = false;

        Assert.IsTrue(newtonsoftReader.Read()); // StartObject
        Assert.IsTrue(newtonsoftReader.Read()); // PropertyName
        Assert.AreEqual("timestamp", newtonsoftReader.Value);
        Assert.IsTrue(newtonsoftReader.Read()); // Date value
        Assert.AreEqual(JsonToken.Date, newtonsoftReader.TokenType);
        
        var readDate = (DateTime)newtonsoftReader.Value!;
        Assert.AreEqual(testDate, readDate.ToUniversalTime());
    }

    /// <summary>
    /// Tests that MiniBson can read DateTime written by Newtonsoft
    /// </summary>
    [TestMethod]
    public void ReadNewtonsoftWrittenDateTime()
    {
        var testDate = new DateTime(2025, 12, 25, 8, 0, 0, DateTimeKind.Utc);

        // Write with Newtonsoft
        using var ms = new MemoryStream();
        using (var writer = new BsonDataWriter(ms) { CloseOutput = false })
        {
            writer.WriteStartObject();
            writer.WritePropertyName("date");
            writer.WriteValue(testDate);
            writer.WriteEndObject();
        }

        // Read with MiniBson
        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("date", reader.CurrentName);
        Assert.AreEqual(BsonType.DateTime, reader.CurrentType);
        
        var readDate = reader.ReadDateTime();
        Assert.AreEqual(testDate, readDate);
    }

    /// <summary>
    /// Tests binary data compatibility
    /// </summary>
    [TestMethod]
    public void BinaryDataRoundTrip()
    {
        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0xAA, 0xBB, 0xCC };

        // Write with MiniBson
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteBinary("data", testData);
            writer.WriteEndDocument();
        }

        // Read with Newtonsoft
        ms.Position = 0;
        using var newtonsoftReader = new BsonDataReader(ms);

        Assert.IsTrue(newtonsoftReader.Read()); // StartObject
        Assert.IsTrue(newtonsoftReader.Read()); // PropertyName
        Assert.AreEqual("data", newtonsoftReader.Value);
        Assert.IsTrue(newtonsoftReader.Read()); // Bytes value
        Assert.AreEqual(JsonToken.Bytes, newtonsoftReader.TokenType);
        
        CollectionAssert.AreEqual(testData, (byte[])newtonsoftReader.Value!);
    }

    /// <summary>
    /// Tests reading binary data written by Newtonsoft
    /// </summary>
    [TestMethod]
    public void ReadNewtonsoftWrittenBinary()
    {
        var testData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        // Write with Newtonsoft
        using var ms = new MemoryStream();
        using (var writer = new BsonDataWriter(ms) { CloseOutput = false })
        {
            writer.WriteStartObject();
            writer.WritePropertyName("bytes");
            writer.WriteValue(testData);
            writer.WriteEndObject();
        }

        // Read with MiniBson
        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("bytes", reader.CurrentName);
        Assert.AreEqual(BsonType.Binary, reader.CurrentType);
        
        var (data, subType) = reader.ReadBinary();
        CollectionAssert.AreEqual(testData, data);
    }

    /// <summary>
    /// Tests null value compatibility
    /// </summary>
    [TestMethod]
    public void NullValueRoundTrip()
    {
        // Write with MiniBson
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteNull("nullField");
            writer.WriteEndDocument();
        }

        // Read with Newtonsoft
        ms.Position = 0;
        using var newtonsoftReader = new BsonDataReader(ms);

        Assert.IsTrue(newtonsoftReader.Read()); // StartObject
        Assert.IsTrue(newtonsoftReader.Read()); // PropertyName
        Assert.AreEqual("nullField", newtonsoftReader.Value);
        Assert.IsTrue(newtonsoftReader.Read()); // Null value
        Assert.AreEqual(JsonToken.Null, newtonsoftReader.TokenType);
    }

    /// <summary>
    /// Tests reading null written by Newtonsoft
    /// </summary>
    [TestMethod]
    public void ReadNewtonsoftWrittenNull()
    {
        // Write with Newtonsoft
        using var ms = new MemoryStream();
        using (var writer = new BsonDataWriter(ms) { CloseOutput = false })
        {
            writer.WriteStartObject();
            writer.WritePropertyName("empty");
            writer.WriteNull();
            writer.WriteEndObject();
        }

        // Read with MiniBson
        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("empty", reader.CurrentName);
        Assert.AreEqual(BsonType.Null, reader.CurrentType);
    }

    /// <summary>
    /// Tests a complex document with mixed types
    /// </summary>
    [TestMethod]
    public void ComplexDocumentRoundTrip()
    {
        // Write with MiniBson
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteString("type", "user");
            writer.WriteInt32("version", 1);
            writer.WriteStartDocument("profile");
            writer.WriteString("username", "testuser");
            writer.WriteBoolean("verified", true);
            writer.WriteEndDocument();
            writer.WriteStartArray("scores");
            writer.WriteInt32(100);
            writer.WriteInt32(95);
            writer.WriteInt32(87);
            writer.WriteEndArray();
            writer.WriteEndDocument();
        }

        // Read with Newtonsoft and serialize to JSON string for verification
        ms.Position = 0;
        using var newtonsoftReader = new BsonDataReader(ms);
        var serializer = new JsonSerializer();
        var obj = serializer.Deserialize<Dictionary<string, object>>(newtonsoftReader);

        Assert.IsNotNull(obj);
        Assert.AreEqual("user", obj["type"]);
        Assert.AreEqual(1L, obj["version"]);
    }

    /// <summary>
    /// Tests reading a complex Newtonsoft-serialized object
    /// </summary>
    [TestMethod]
    public void ReadNewtonsoftSerializedObject()
    {
        var testObject = new
        {
            name = "Test",
            count = 42,
            enabled = true,
            tags = new[] { "a", "b", "c" }
        };

        // Write with Newtonsoft
        using var ms = new MemoryStream();
        using (var writer = new BsonDataWriter(ms) { CloseOutput = false })
        {
            var serializer = new JsonSerializer();
            serializer.Serialize(writer, testObject);
        }

        // Read with MiniBson
        ms.Position = 0;
        using var reader = new BsonReader(ms);
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("name", reader.CurrentName);
        Assert.AreEqual("Test", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("count", reader.CurrentName);
        Assert.AreEqual(42, reader.ReadInt64()); // Newtonsoft writes as Int64

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("enabled", reader.CurrentName);
        Assert.IsTrue(reader.ReadBoolean());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("tags", reader.CurrentName);
        reader.ReadStartArray();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("a", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("b", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("c", reader.ReadString());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument(); // End array

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument(); // End document
    }
}

