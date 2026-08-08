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

// Enum types for testing
public enum Status
{
    Unknown = 0,
    Active = 1,
    Inactive = 2,
    Pending = 3
}

public enum Priority : byte
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum LargeEnum : long
{
    Small = 1,
    Large = 1000000000000L
}

public class TypeWithEnums
{
    public Status Status { get; set; }
    public Priority Priority { get; set; }
    public Status? NullableStatus { get; set; }
}

public class TypeWithEnumArrays
{
    public Status[] Statuses { get; set; } = [];
    public Priority[] Priorities { get; set; } = [];
}

public class TypeWithLargeEnum
{
    public LargeEnum Value { get; set; }
    public LargeEnum[] Values { get; set; } = [];
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
[BsonSerializable(typeof(TypeWithEnums))]
[BsonSerializable(typeof(TypeWithEnumArrays))]
[BsonSerializable(typeof(TypeWithLargeEnum))]
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

    private void Serialize(object input, Stream output)
    {
        // The writer compares the measured length against the bytes written for each document.
        // Thus each test here also tests the measure pass against the write pass.
        var bytes = BsonTestWriter.Raw(writer => _context.Serialize(input, writer));
        Assert.AreEqual(bytes.Length, _context.GetSerializedSize(input),
            "GetSerializedSize disagrees with the bytes actually written.");
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Deserializes the same bytes two times: from one piece, and from a sequence of small
    /// segments. A value that lies across a segment boundary takes a different path in the
    /// reader, and one adjacent buffer never runs that path.
    /// </summary>
    private object? Deserialize(Stream input, Type type)
    {
        var document = Drain(input);

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

    private static byte[] Drain(Stream input)
    {
        if (input is MemoryStream memory)
            return memory.ToArray();

        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    [TestMethod]
    public void SerializeAndDeserializeSimpleType()
    {
        var original = new SimpleType
        {
            Name = "Test",
            Age = 25,
            IsActive = true
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (SimpleType?)Deserialize(ms, typeof(SimpleType));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithNullables?)Deserialize(ms, typeof(TypeWithNullables));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithNullables?)Deserialize(ms, typeof(TypeWithNullables));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithArrays?)Deserialize(ms, typeof(TypeWithArrays));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (NestedType?)Deserialize(ms, typeof(NestedType));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (NestedType?)Deserialize(ms, typeof(NestedType));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (ComplexType?)Deserialize(ms, typeof(ComplexType));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithArrays?)Deserialize(ms, typeof(TypeWithArrays));

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Numbers.Length);
        Assert.AreEqual(0, result.Tags.Length);
    }

    [TestMethod]
    public void SerializeUnsupportedTypeThrows()
    {
        using var ms = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => Serialize("unsupported string", ms));
    }

    [TestMethod]
    public void DeserializeUnsupportedTypeThrows()
    {
        // Write some valid BSON
        var empty = BsonTestWriter.Serialize(_ => { });
        using var ms = new MemoryStream(empty);

        Assert.Throws<NotSupportedException>(() => Deserialize(ms, typeof(string)));
    }

    [TestMethod]
    public void SerializeAndDeserializeBinaryData()
    {
        var original = new TypeWithBinaryData
        {
            Data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0xAA, 0xBB, 0xCC },
            Name = "BinaryTest"
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithBinaryData?)Deserialize(ms, typeof(TypeWithBinaryData));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithBinaryData?)Deserialize(ms, typeof(TypeWithBinaryData));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithBinaryData?)Deserialize(ms, typeof(TypeWithBinaryData));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithBinaryData?)Deserialize(ms, typeof(TypeWithBinaryData));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithBinaryData?)Deserialize(ms, typeof(TypeWithBinaryData));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(ms, typeof(TypeWithReadOnlyMemoryBinary));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(ms, typeof(TypeWithReadOnlyMemoryBinary));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(ms, typeof(TypeWithReadOnlyMemoryBinary));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithReadOnlyMemoryBinary?)Deserialize(ms, typeof(TypeWithReadOnlyMemoryBinary));

        Assert.IsNotNull(result);
        Assert.IsFalse(result.NullableData.HasValue);
    }

    [TestMethod]
    public void SerializeAndDeserializeEnums()
    {
        var original = new TypeWithEnums
        {
            Status = Status.Active,
            Priority = Priority.High,
            NullableStatus = Status.Pending
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithEnums?)Deserialize(ms, typeof(TypeWithEnums));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Status, result.Status);
        Assert.AreEqual(original.Priority, result.Priority);
        Assert.AreEqual(original.NullableStatus, result.NullableStatus);
    }

    [TestMethod]
    public void SerializeAndDeserializeEnums_WithNullNullable()
    {
        var original = new TypeWithEnums
        {
            Status = Status.Inactive,
            Priority = Priority.Low,
            NullableStatus = null
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithEnums?)Deserialize(ms, typeof(TypeWithEnums));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Status, result.Status);
        Assert.AreEqual(original.Priority, result.Priority);
        Assert.IsNull(result.NullableStatus);
    }

    [TestMethod]
    public void SerializeAndDeserializeEnumArrays()
    {
        var original = new TypeWithEnumArrays
        {
            Statuses = [Status.Active, Status.Inactive, Status.Pending],
            Priorities = [Priority.Low, Priority.High, Priority.Critical]
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithEnumArrays?)Deserialize(ms, typeof(TypeWithEnumArrays));

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Statuses, result.Statuses);
        CollectionAssert.AreEqual(original.Priorities, result.Priorities);
    }

    [TestMethod]
    public void SerializeAndDeserializeEmptyEnumArrays()
    {
        var original = new TypeWithEnumArrays
        {
            Statuses = [],
            Priorities = []
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithEnumArrays?)Deserialize(ms, typeof(TypeWithEnumArrays));

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Statuses.Length);
        Assert.AreEqual(0, result.Priorities.Length);
    }

    [TestMethod]
    public void SerializeAndDeserializeLargeEnum()
    {
        var original = new TypeWithLargeEnum
        {
            Value = LargeEnum.Large,
            Values = [LargeEnum.Small, LargeEnum.Large]
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithLargeEnum?)Deserialize(ms, typeof(TypeWithLargeEnum));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Value, result.Value);
        CollectionAssert.AreEqual(original.Values, result.Values);
    }

    [TestMethod]
    public void EnumSerializedAsInt_VerifyBsonFormat()
    {
        var original = new TypeWithEnums
        {
            Status = Status.Active,
            Priority = Priority.High,
            NullableStatus = null
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        // Read back with raw BsonReader to verify it's stored as int
        ms.Position = 0;
        var reader = new BsonReader(ms.ToArray());
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Status", reader.CurrentName);
        Assert.AreEqual(BsonType.Int32, reader.CurrentType);
        Assert.AreEqual((int)Status.Active, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Priority", reader.CurrentName);
        Assert.AreEqual(BsonType.Int32, reader.CurrentType);
        Assert.AreEqual((int)Priority.High, reader.ReadInt32());

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("NullableStatus", reader.CurrentName);
        Assert.AreEqual(BsonType.Null, reader.CurrentType);
    }

    [TestMethod]
    public void LargeEnumSerializedAsInt64_VerifyBsonFormat()
    {
        var original = new TypeWithLargeEnum
        {
            Value = LargeEnum.Large,
            Values = []
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        // Read back with raw BsonReader to verify it's stored as int64
        ms.Position = 0;
        var reader = new BsonReader(ms.ToArray());
        reader.ReadStartDocument();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Value", reader.CurrentName);
        Assert.AreEqual(BsonType.Int64, reader.CurrentType);
        Assert.AreEqual((long)LargeEnum.Large, reader.ReadInt64());
    }

    [TestMethod]
    public void SerializeAndDeserializeType1()
    {
        var original = new Type1
        {
            Name = "Base",
            Value = 42
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (Type1?)Deserialize(ms, typeof(Type1));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(original.Value, result.Value);
    }

    [TestMethod]
    public void SerializeAndDeserializeType2_InheritsWithNoNewProperties()
    {
        var original = new Type2
        {
            Name = "Derived",
            Value = 100
        };

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (Type2?)Deserialize(ms, typeof(Type2));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (Type3?)Deserialize(ms, typeof(Type3));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        // Verify with raw reader that all 4 properties are serialized
        ms.Position = 0;
        var reader = new BsonReader(ms.ToArray());
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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (SimpleRecord?)Deserialize(ms, typeof(SimpleRecord));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.AreEqual(original.Value, result.Value);
    }

    [TestMethod]
    public void RecordSerializesBsonCorrectly()
    {
        var original = new SimpleRecord("Test", 100);

        using var ms = new MemoryStream();
        Serialize(original, ms);

        // Verify BSON structure with raw reader
        ms.Position = 0;
        var reader = new BsonReader(ms.ToArray());
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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (RecordWithNullables?)Deserialize(ms, typeof(RecordWithNullables));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.NullableName, result.NullableName);
        Assert.AreEqual(original.NullableValue, result.NullableValue);
    }

    [TestMethod]
    public void SerializeAndDeserializeRecordWithNullables_WithNulls()
    {
        var original = new RecordWithNullables(null, null);

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (RecordWithNullables?)Deserialize(ms, typeof(RecordWithNullables));

        Assert.IsNotNull(result);
        Assert.IsNull(result.NullableName);
        Assert.IsNull(result.NullableValue);
    }

    [TestMethod]
    public void RecordWithNullablesSerializesBsonCorrectly()
    {
        var original = new RecordWithNullables("Present", null);

        using var ms = new MemoryStream();
        Serialize(original, ms);

        // Verify BSON structure
        ms.Position = 0;
        var reader = new BsonReader(ms.ToArray());
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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (RecordWithArray?)Deserialize(ms, typeof(RecordWithArray));

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Numbers, result.Numbers);
        CollectionAssert.AreEqual(original.Tags, result.Tags);
    }

    [TestMethod]
    public void RecordWithArraySerializesBsonCorrectly()
    {
        var original = new RecordWithArray(new[] { 10, 20 }, new[] { "x", "y" });

        using var ms = new MemoryStream();
        Serialize(original, ms);

        // Verify BSON structure
        ms.Position = 0;
        var reader = new BsonReader(ms.ToArray());
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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (NestedRecord?)Deserialize(ms, typeof(NestedRecord));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (NestedRecord?)Deserialize(ms, typeof(NestedRecord));

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Title, result.Title);
        Assert.IsNull(result.Inner);
    }

    [TestMethod]
    public void NestedRecordSerializesBsonCorrectly()
    {
        var original = new NestedRecord("Outer", new SimpleRecord("Inner", 50));

        using var ms = new MemoryStream();
        Serialize(original, ms);

        // Verify BSON structure
        ms.Position = 0;
        var reader = new BsonReader(ms.ToArray());
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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (SimpleRecord?)Deserialize(ms, typeof(SimpleRecord));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (ComplexType?)Deserialize(ms, typeof(ComplexType));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithBinaryData?)Deserialize(ms, typeof(TypeWithBinaryData));

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

        using var ms = new MemoryStream();
        Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithArrays?)Deserialize(ms, typeof(TypeWithArrays));

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

        using var ms = new MemoryStream();
        Serialize(head!, ms);

        ms.Position = 0;
        var result = (LinkedNode?)Deserialize(ms, typeof(LinkedNode));

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
