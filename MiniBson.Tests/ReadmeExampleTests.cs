using System.Buffers;

namespace MiniBson.Tests;

/// <summary>
/// The examples from README.md, compiled and run.
/// </summary>
/// <remarks>
/// A reader of the README copies these examples, and the low-level example contains arithmetic
/// that can be wrong. Keep this file and the README in step.
/// </remarks>
[TestClass]
public sealed class ReadmeExampleTests
{
    public sealed class Person
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public string[] Tags { get; set; } = [];
    }

    /// <summary>The "Source-generated serialization" round trip.</summary>
    [TestMethod]
    public void SourceGeneratedRoundTripExample()
    {
        var context = new ReadmeBsonContext();
        var original = new Person
        {
            Name = "Ada",
            Age = 37,
            Tags = ["compiler", "math"]
        };

        var output = new ArrayBufferWriter<byte>(context.GetSerializedSize(original));
        context.Serialize(original, new BsonWriter(output));

        var reader = new BsonReader(output.WrittenSpan);
        var copy = (Person?)context.Deserialize(ref reader, typeof(Person));

        Assert.IsNotNull(copy);
        Assert.AreEqual("Ada", copy.Name);
        Assert.AreEqual(37, copy.Age);
        CollectionAssert.AreEqual(original.Tags, copy.Tags);
    }

    /// <summary>The "Write a document" example, including its BsonSize arithmetic.</summary>
    [TestMethod]
    public void LowLevelWriteExample()
    {
        var tagsLength = BsonSize.ArrayOverhead(2)
            + BsonSize.String("compiler") + BsonSize.String("math");

        var length = BsonSize.DocumentOverhead
            + BsonSize.Element("name") + BsonSize.String("Ada")
            + BsonSize.Element("age") + BsonSize.Int32
            + BsonSize.Element("active") + BsonSize.Boolean
            + BsonSize.Element("tags") + tagsLength;

        var output = new ArrayBufferWriter<byte>(length);
        var writer = new BsonWriter(output);

        writer.WriteStartDocument(length);
        writer.WriteString("name", "Ada");
        writer.WriteInt32("age", 37);
        writer.WriteBoolean("active", true);

        writer.WriteStartArray("tags", tagsLength);
        writer.WriteString("compiler");
        writer.WriteString("math");
        writer.WriteEndArray();

        writer.WriteEndDocument();

        var bson = output.WrittenSpan.ToArray();

        // The writer throws on a wrong length. Thus this point in the test already shows that the
        // arithmetic above is correct. These two assertions also fix the total.
        Assert.AreEqual(length, bson.Length);
        Assert.AreEqual(length, BitConverter.ToInt32(bson, 0));

        LowLevelReadExample(bson);
    }

    /// <summary>The "Read a document" example.</summary>
    private static void LowLevelReadExample(byte[] bson)
    {
        var seen = new List<string>();

        var reader = new BsonReader(bson);
        reader.ReadStartDocument();

        while (reader.Read())
        {
            switch (reader.CurrentName)
            {
                case "name":
                    seen.Add(reader.ReadString());
                    break;
                case "age":
                    seen.Add(reader.ReadInt32().ToString());
                    break;
                case "tags":
                    reader.ReadStartArray();
                    while (reader.Read())
                    {
                        seen.Add(reader.ReadString());
                    }
                    reader.ReadEndDocument();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        reader.ReadEndDocument();

        CollectionAssert.AreEqual(new[] { "Ada", "37", "compiler", "math" }, seen);
    }

}

[BsonSerializable(typeof(ReadmeExampleTests.Person))]
public partial class ReadmeBsonContext;
