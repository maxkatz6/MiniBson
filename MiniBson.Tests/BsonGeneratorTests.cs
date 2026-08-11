using MiniBson;

namespace MiniBson.Tests;

// Test types
public class SimpleType
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public bool IsActive { get; set; }
}

public class TypeWithNullables
{
    public string? NullableString { get; set; }
    public int? NullableInt { get; set; }
    public DateTime? NullableDate { get; set; }
}

public class TypeWithArrays
{
    public int[] Numbers { get; set; } = [];
    public string[] Tags { get; set; } = [];
}

public class NestedType
{
    public string Title { get; set; } = string.Empty;
    public SimpleType? Inner { get; set; }
}

public class ComplexType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Score { get; set; }
    public DateTime CreatedAt { get; set; }
    public SimpleType[] Items { get; set; } = [];
    public NestedType? Nested { get; set; }
}

public class TypeWithBinaryData
{
    public byte[] Data { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public byte[]? NullableData { get; set; }
}

public class TypeWithReadOnlyMemoryBinary
{
    public ReadOnlyMemory<byte> Data { get; set; }
    public string Name { get; set; } = string.Empty;
    public ReadOnlyMemory<byte>? NullableData { get; set; }
}

// Inheritance test types
public class Type1
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class Type2 : Type1
{
    // No new properties
}

public class Type3 : Type1
{
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

// Self-referencing: the shape where the measure pass does the most repeated work.
public class LinkedNode
{
    public string Label { get; set; } = string.Empty;
    public int Depth { get; set; }
    public LinkedNode? Next { get; set; }
}

// Record types for testing
public record SimpleRecord(string Name, int Value);

public record RecordWithNullables(string? NullableName, int? NullableValue);

public record RecordWithArray(int[] Numbers, string[] Tags);

public record NestedRecord(string Title, SimpleRecord? Inner);

// Generated context
[BsonSerializable(typeof(SimpleType))]
[BsonSerializable(typeof(TypeWithNullables))]
[BsonSerializable(typeof(TypeWithArrays))]
[BsonSerializable(typeof(NestedType))]
[BsonSerializable(typeof(ComplexType))]
[BsonSerializable(typeof(TypeWithBinaryData))]
[BsonSerializable(typeof(TypeWithReadOnlyMemoryBinary))]
[BsonSerializable(typeof(Type1))]
[BsonSerializable(typeof(Type2))]
[BsonSerializable(typeof(Type3))]
[BsonSerializable(typeof(LinkedNode))]
[BsonSerializable(typeof(SimpleRecord))]
[BsonSerializable(typeof(RecordWithNullables))]
[BsonSerializable(typeof(RecordWithArray))]
[BsonSerializable(typeof(NestedRecord))]
public partial class TestBsonContext;

[TestClass]
public sealed class BsonGeneratorTests
{
    private readonly TestBsonContext _context = new();

    private byte[] Serialize(object input)
    {
        // The writer compares the measured length against the bytes written for each document.
        // Thus each test here also tests the measure pass against the write pass.
        var bytes = BsonTestWriter.Raw(writer => _context.Serialize(input, writer));
        Assert.AreEqual(bytes.Length, _context.GetSerializedSize(input),
            "GetSerializedSize disagrees with the bytes actually written.");
        return bytes;
    }

    /// <summary>
    /// Deserializes the same bytes two times: from one piece, and from a sequence of small
    /// segments. A value that lies across a segment boundary takes a different path in the
    /// reader, and one adjacent buffer never runs that path.
    /// </summary>
    private object? Deserialize(byte[] document, Type type)
    {
        var contiguous = DeserializeFrom(new BsonReader(document), type);
        var fragmented = DeserializeFrom(new BsonReader(SequenceFactory.Chunked(document, 3)), type);

        if (contiguous is null || fragmented is null)
        {
            Assert.AreEqual(contiguous, fragmented, "Deserializing from a segmented input produced a different value.");
            return contiguous;
        }

        // These models are mostly classes without value equality, so compare what they encode
        // back to rather than the instances themselves.
        CollectionAssert.AreEqual(
            BsonTestWriter.Raw(w => _context.Serialize(contiguous, w)),
            BsonTestWriter.Raw(w => _context.Serialize(fragmented, w)),
            "Deserializing from a segmented input produced a different value.");

        return contiguous;
    }

    private object? DeserializeFrom(BsonReader reader, Type type) => _context.Deserialize(ref reader, type);

    [TestMethod]
    public void SerializeAndDeserializeSimpleType()
    {
        var original = new SimpleType
        {
            Name = "Test",
            Age = 25,
            IsActive = true
        };

        var result = (SimpleType?)Deserialize(Serialize(original), typeof(SimpleType));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(original.Age, result.Age);
        Assert.AreEqual(original.IsActive, result.IsActive);
    }

    [TestMethod]
    public void SerializeAndDeserializeTypeWithNullables_WithValues()
    {
        var original = new TypeWithNullables
        {
            NullableString = "Hello",
            NullableInt = 42,
            NullableDate = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc)
        };

        var result = (TypeWithNullables?)Deserialize(Serialize(original), typeof(TypeWithNullables));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.NullableString, result.NullableString);
        Assert.AreEqual(original.NullableInt, result.NullableInt);
        Assert.AreEqual(original.NullableDate, result.NullableDate);
    }

    [TestMethod]
    public void SerializeAndDeserializeTypeWithNullables_WithNulls()
    {
        var original = new TypeWithNullables
        {
            NullableString = null,
            NullableInt = null,
            NullableDate = null
        };

        var result = (TypeWithNullables?)Deserialize(Serialize(original), typeof(TypeWithNullables));

        Assert.IsNotNull(result);
        Assert.IsNull(result.NullableString);
        Assert.IsNull(result.NullableInt);
        Assert.IsNull(result.NullableDate);
    }

    [TestMethod]
    public void SerializeAndDeserializeTypeWithArrays()
    {
        var original = new TypeWithArrays
        {
            Numbers = [1, 2, 3, 4, 5],
            Tags = ["a", "b", "c"]
        };

        var result = (TypeWithArrays?)Deserialize(Serialize(original), typeof(TypeWithArrays));

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Numbers, result.Numbers);
        CollectionAssert.AreEqual(original.Tags, result.Tags);
    }

    [TestMethod]
    public void SerializeAndDeserializeNestedType()
    {
        var original = new NestedType
        {
            Title = "Parent",
            Inner = new SimpleType
            {
                Name = "Child",
                Age = 10,
                IsActive = false
            }
        };

        var result = (NestedType?)Deserialize(Serialize(original), typeof(NestedType));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Title, result.Title);
        Assert.IsNotNull(result.Inner);
        Assert.AreEqual(original.Inner.Name, result.Inner.Name);
        Assert.AreEqual(original.Inner.Age, result.Inner.Age);
        Assert.AreEqual(original.Inner.IsActive, result.Inner.IsActive);
    }

    [TestMethod]
    public void SerializeAndDeserializeNestedType_WithNullInner()
    {
        var original = new NestedType
        {
            Title = "Parent",
            Inner = null
        };

        var result = (NestedType?)Deserialize(Serialize(original), typeof(NestedType));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Title, result.Title);
        Assert.IsNull(result.Inner);
    }

    [TestMethod]
    public void SerializeAndDeserializeComplexType()
    {
        var original = new ComplexType
        {
            Id = Guid.NewGuid(),
            Name = "Complex",
            Score = 95.5,
            CreatedAt = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc),
            Items =
            [
                new SimpleType { Name = "Item1", Age = 1, IsActive = true },
                new SimpleType { Name = "Item2", Age = 2, IsActive = false }
            ],
            Nested = new NestedType
            {
                Title = "NestedTitle",
                Inner = new SimpleType { Name = "DeepNested", Age = 100, IsActive = true }
            }
        };

        var result = (ComplexType?)Deserialize(Serialize(original), typeof(ComplexType));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Id, result.Id);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(original.Score, result.Score, 0.001);
        Assert.AreEqual(original.CreatedAt, result.CreatedAt);
        Assert.AreEqual(original.Items.Length, result.Items.Length);
        Assert.AreEqual(original.Items[0].Name, result.Items[0].Name);
        Assert.AreEqual(original.Items[1].Name, result.Items[1].Name);
        Assert.IsNotNull(result.Nested);
        Assert.AreEqual(original.Nested.Title, result.Nested.Title);
        Assert.IsNotNull(result.Nested.Inner);
        Assert.AreEqual(original.Nested.Inner.Name, result.Nested.Inner.Name);
    }

    [TestMethod]
    public void SerializeAndDeserializeEmptyArrays()
    {
        var original = new TypeWithArrays
        {
            Numbers = [],
            Tags = []
        };

        var result = (TypeWithArrays?)Deserialize(Serialize(original), typeof(TypeWithArrays));

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Numbers.Length);
        Assert.AreEqual(0, result.Tags.Length);
    }

    [TestMethod]
    public void SerializeUnsupportedTypeThrows()
    {
        Assert.Throws<NotSupportedException>(() => Serialize("unsupported string"));
    }

    [TestMethod]
    public void DeserializeUnsupportedTypeThrows()
    {
        // Valid BSON, but the type is not registered.
        var empty = BsonTestWriter.Serialize(_ => { });

        Assert.Throws<NotSupportedException>(() => Deserialize(empty, typeof(string)));
    }

    [TestMethod]
    public void SerializeAndDeserializeBinaryData()
    {
        var original = new TypeWithBinaryData
        {
            Data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0xAA, 0xBB, 0xCC },
            Name = "BinaryTest"
        };

        var result = (TypeWithBinaryData?)Deserialize(Serialize(original), typeof(TypeWithBinaryData));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        CollectionAssert.AreEqual(original.Data, result.Data);
    }

    [TestMethod]
    public void SerializeAndDeserializeEmptyBinaryData()
    {
        var original = new TypeWithBinaryData
        {
            Data = [],
            Name = "EmptyBinary"
        };

        var result = (TypeWithBinaryData?)Deserialize(Serialize(original), typeof(TypeWithBinaryData));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(0, result.Data.Length);
    }

    [TestMethod]
    public void SerializeAndDeserializeNullableBinaryData_WithValue()
    {
        var original = new TypeWithBinaryData
        {
            Data = new byte[] { 0xFF },
            Name = "NullableTest",
            NullableData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }
        };

        var result = (TypeWithBinaryData?)Deserialize(Serialize(original), typeof(TypeWithBinaryData));

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.NullableData);
        CollectionAssert.AreEqual(original.NullableData, result.NullableData);
    }

    [TestMethod]
    public void SerializeAndDeserializeNullableBinaryData_WithNull()
    {
        var original = new TypeWithBinaryData
        {
            Data = new byte[] { 0x01 },
            Name = "NullableNullTest",
            NullableData = null
        };

        var result = (TypeWithBinaryData?)Deserialize(Serialize(original), typeof(TypeWithBinaryData));

        Assert.IsNotNull(result);
        Assert.IsNull(result.NullableData);
    }

    [TestMethod]
    public void SerializeAndDeserializeLargeBinaryData()
    {
        var largeData = new byte[10000];
        new Random(42).NextBytes(largeData);

        var original = new TypeWithBinaryData
        {
            Data = largeData,
            Name = "LargeBinary"
        };

        var result = (TypeWithBinaryData?)Deserialize(Serialize(original), typeof(TypeWithBinaryData));

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Data, result.Data);
    }

    [TestMethod]
    public void SerializeAndDeserializeReadOnlyMemoryBinary()
    {
        var original = new TypeWithReadOnlyMemoryBinary
        {
            Data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0xAA, 0xBB, 0xCC },
            Name = "MemoryBinaryTest"
        };

        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(Serialize(original), typeof(TypeWithReadOnlyMemoryBinary));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        CollectionAssert.AreEqual(original.Data.ToArray(), result.Data.ToArray());
    }

    [TestMethod]
    public void SerializeAndDeserializeReadOnlyMemoryBinary_Empty()
    {
        var original = new TypeWithReadOnlyMemoryBinary
        {
            Data = ReadOnlyMemory<byte>.Empty,
            Name = "EmptyMemory"
        };

        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(Serialize(original), typeof(TypeWithReadOnlyMemoryBinary));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(0, result.Data.Length);
    }

    [TestMethod]
    public void SerializeAndDeserializeReadOnlyMemoryBinary_NullableWithValue()
    {
        var original = new TypeWithReadOnlyMemoryBinary
        {
            Data = new byte[] { 0xFF },
            Name = "NullableMemoryTest",
            NullableData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }
        };

        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(Serialize(original), typeof(TypeWithReadOnlyMemoryBinary));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.NullableData.HasValue);
        CollectionAssert.AreEqual(original.NullableData!.Value.ToArray(), result.NullableData.Value.ToArray());
    }

    [TestMethod]
    public void SerializeAndDeserializeReadOnlyMemoryBinary_NullableWithNull()
    {
        var original = new TypeWithReadOnlyMemoryBinary
        {
            Data = new byte[] { 0x01 },
            Name = "NullableNullMemoryTest",
            NullableData = null
        };

        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(Serialize(original), typeof(TypeWithReadOnlyMemoryBinary));

        Assert.IsNotNull(result);
        Assert.IsFalse(result.NullableData.HasValue);
    }

    [TestMethod]
    public void SerializeAndDeserializeType2_InheritsWithNoNewProperties()
    {
        var original = new Type2
        {
            Name = "Derived",
            Value = 100
        };

        var result = (Type2?)Deserialize(Serialize(original), typeof(Type2));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(original.Value, result.Value);
    }

    [TestMethod]
    public void SerializeAndDeserializeType3_InheritsWithNewProperties()
    {
        var original = new Type3
        {
            Name = "Extended",
            Value = 200,
            Description = "This is a type with new properties",
            CreatedAt = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc)
        };

        var result = (Type3?)Deserialize(Serialize(original), typeof(Type3));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(original.Value, result.Value);
        Assert.AreEqual(original.Description, result.Description);
        Assert.AreEqual(original.CreatedAt, result.CreatedAt);
    }

    [TestMethod]
    public void InheritedTypesSerializeAllProperties()
    {
        var original = new Type3
        {
            Name = "Test",
            Value = 50,
            Description = "Verify all props",
            CreatedAt = DateTime.UtcNow
        };

        var reader = new BsonReader(Serialize(original));
        reader.ReadStartDocument();

        var propertiesRead = new HashSet<string>();
        while (reader.Read())
        {
            propertiesRead.Add(reader.CurrentName);
            reader.Skip();
        }

        Assert.AreEqual(4, propertiesRead.Count);
        Assert.IsTrue(propertiesRead.Contains("Name"));
        Assert.IsTrue(propertiesRead.Contains("Value"));
        Assert.IsTrue(propertiesRead.Contains("Description"));
        Assert.IsTrue(propertiesRead.Contains("CreatedAt"));
    }

    [TestMethod]
    public void SerializeAndDeserializeSimpleRecord()
    {
        var original = new SimpleRecord("RecordName", 42);

        var result = (SimpleRecord?)Deserialize(Serialize(original), typeof(SimpleRecord));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(original.Value, result.Value);
    }

    [TestMethod]
    public void RecordSerializesBsonCorrectly()
    {
        var original = new SimpleRecord("Test", 100);

        var reader = new BsonReader(Serialize(original));
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Name", reader.CurrentName);
        Assert.AreEqual(BsonType.String, reader.CurrentType);
        Assert.AreEqual("Test", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Value", reader.CurrentName);
        Assert.AreEqual(BsonType.Int32, reader.CurrentType);
        Assert.AreEqual(100, reader.ReadInt32());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void SerializeAndDeserializeRecordWithNullables_WithValues()
    {
        var original = new RecordWithNullables("NotNull", 123);

        var result = (RecordWithNullables?)Deserialize(Serialize(original), typeof(RecordWithNullables));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.NullableName, result.NullableName);
        Assert.AreEqual(original.NullableValue, result.NullableValue);
    }

    [TestMethod]
    public void SerializeAndDeserializeRecordWithNullables_WithNulls()
    {
        var original = new RecordWithNullables(null, null);

        var result = (RecordWithNullables?)Deserialize(Serialize(original), typeof(RecordWithNullables));

        Assert.IsNotNull(result);
        Assert.IsNull(result.NullableName);
        Assert.IsNull(result.NullableValue);
    }

    [TestMethod]
    public void RecordWithNullablesSerializesBsonCorrectly()
    {
        var original = new RecordWithNullables("Present", null);

        var reader = new BsonReader(Serialize(original));
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("NullableName", reader.CurrentName);
        Assert.AreEqual(BsonType.String, reader.CurrentType);
        Assert.AreEqual("Present", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("NullableValue", reader.CurrentName);
        Assert.AreEqual(BsonType.Null, reader.CurrentType);

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void SerializeAndDeserializeRecordWithArray()
    {
        var original = new RecordWithArray(new[] { 1, 2, 3 }, new[] { "a", "b", "c" });

        var result = (RecordWithArray?)Deserialize(Serialize(original), typeof(RecordWithArray));

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Numbers, result.Numbers);
        CollectionAssert.AreEqual(original.Tags, result.Tags);
    }

    [TestMethod]
    public void RecordWithArraySerializesBsonCorrectly()
    {
        var original = new RecordWithArray(new[] { 10, 20 }, new[] { "x", "y" });

        var reader = new BsonReader(Serialize(original));
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Numbers", reader.CurrentName);
        Assert.AreEqual(BsonType.Array, reader.CurrentType);
        reader.ReadStartArray();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(10, reader.ReadInt32());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(20, reader.ReadInt32());
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Tags", reader.CurrentName);
        Assert.AreEqual(BsonType.Array, reader.CurrentType);
        reader.ReadStartArray();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("x", reader.ReadString());
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("y", reader.ReadString());
        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void SerializeAndDeserializeNestedRecord()
    {
        var inner = new SimpleRecord("Child", 999);
        var original = new NestedRecord("Parent", inner);

        var result = (NestedRecord?)Deserialize(Serialize(original), typeof(NestedRecord));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Title, result.Title);
        Assert.IsNotNull(result.Inner);
        Assert.AreEqual(inner.Name, result.Inner.Name);
        Assert.AreEqual(inner.Value, result.Inner.Value);
    }

    [TestMethod]
    public void SerializeAndDeserializeNestedRecord_WithNullInner()
    {
        var original = new NestedRecord("Lonely", null);

        var result = (NestedRecord?)Deserialize(Serialize(original), typeof(NestedRecord));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Title, result.Title);
        Assert.IsNull(result.Inner);
    }

    [TestMethod]
    public void NestedRecordSerializesBsonCorrectly()
    {
        var original = new NestedRecord("Outer", new SimpleRecord("Inner", 50));

        var reader = new BsonReader(Serialize(original));
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Title", reader.CurrentName);
        Assert.AreEqual(BsonType.String, reader.CurrentType);
        Assert.AreEqual("Outer", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Inner", reader.CurrentName);
        Assert.AreEqual(BsonType.Document, reader.CurrentType);
        reader.ReadStartNestedDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Name", reader.CurrentName);
        Assert.AreEqual("Inner", reader.ReadString());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Value", reader.CurrentName);
        Assert.AreEqual(50, reader.ReadInt32());

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();

        Assert.IsFalse(reader.Read());
        reader.ReadEndDocument();
    }

    [TestMethod]
    public void RecordsAreImmutable_DifferentInstanceAfterDeserialization()
    {
        var original = new SimpleRecord("Original", 1);

        var result = (SimpleRecord?)Deserialize(Serialize(original), typeof(SimpleRecord));

        Assert.IsNotNull(result);
        // Records use value equality
        Assert.AreEqual(original, result);
        Assert.AreNotSame(original, result);
    }

    /// <summary>
    /// A reference property with no nullable annotation can still hold a null. BSON has no
    /// encoding of a null as a string, as binary data, or as a document. Thus both passes must
    /// agree to write BSON Null for it. In a disagreement, the measure pass gives a length that
    /// the write pass cannot then write.
    /// </summary>
    [TestMethod]
    public void NullInANonNullableReferencePropertyIsWrittenAsNull()
    {
        var original = new ComplexType
        {
            Id = Guid.NewGuid(),
            Name = null!,
            Score = 1.5,
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Items = null!,
            Nested = new NestedType { Title = null!, Inner = null },
        };

        var result = (ComplexType?)Deserialize(Serialize(original), typeof(ComplexType));

        Assert.IsNotNull(result);
        Assert.IsNull(result.Name);
        Assert.IsNull(result.Items);
        Assert.AreEqual(original.Score, result.Score);
        Assert.IsNotNull(result.Nested);
        Assert.IsNull(result.Nested.Title);
    }

    [TestMethod]
    public void NullInANonNullableBinaryPropertyIsWrittenAsNull()
    {
        var original = new TypeWithBinaryData { Data = null!, Name = "named" };

        var result = (TypeWithBinaryData?)Deserialize(Serialize(original), typeof(TypeWithBinaryData));

        Assert.IsNotNull(result);
        Assert.IsNull(result.Data);
        Assert.AreEqual("named", result.Name);
    }

    [TestMethod]
    public void NullElementsInANonNullableStringArrayAreWrittenAsNull()
    {
        var original = new TypeWithArrays
        {
            Numbers = [1, 2],
            Tags = ["a", null!, "c"],
        };

        var result = (TypeWithArrays?)Deserialize(Serialize(original), typeof(TypeWithArrays));

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(new[] { "a", null, "c" }, result.Tags);
    }

    // Self-referencing models nest as deeply as the data, and each level is measured
    // independently, so sizes have to agree with what is written all the way down.
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(50)]
    [DataRow(500)]
    public void SerializeAndDeserializeSelfReferencingType(int depth)
    {
        LinkedNode? head = null;
        for (var i = depth - 1; i >= 0; i--)
            head = new LinkedNode { Label = "node" + i, Depth = i, Next = head };

        var result = (LinkedNode?)Deserialize(Serialize(head!), typeof(LinkedNode));

        for (var i = 0; i < depth; i++)
        {
            Assert.IsNotNull(result, $"Chain ended early at depth {i}.");
            Assert.AreEqual("node" + i, result.Label);
            Assert.AreEqual(i, result.Depth);
            result = result.Next;
        }

        Assert.IsNull(result, "Chain should end after the final node.");
    }
}
