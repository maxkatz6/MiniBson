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

// Generated context
[BsonSerializable(typeof(SimpleType))]
[BsonSerializable(typeof(TypeWithNullables))]
[BsonSerializable(typeof(TypeWithArrays))]
[BsonSerializable(typeof(NestedType))]
[BsonSerializable(typeof(ComplexType))]
public partial class TestBsonContext;

[TestClass]
public sealed class BsonGeneratorTests
{
    private readonly TestBsonContext _context = new();

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (SimpleType?)_context.Deserialize(ms, typeof(SimpleType));

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithNullables?)_context.Deserialize(ms, typeof(TypeWithNullables));

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithNullables?)_context.Deserialize(ms, typeof(TypeWithNullables));

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithArrays?)_context.Deserialize(ms, typeof(TypeWithArrays));

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (NestedType?)_context.Deserialize(ms, typeof(NestedType));

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (NestedType?)_context.Deserialize(ms, typeof(NestedType));

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (ComplexType?)_context.Deserialize(ms, typeof(ComplexType));

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
        _context.Serialize(original, ms);

        ms.Position = 0;
        var result = (TypeWithArrays?)_context.Deserialize(ms, typeof(TypeWithArrays));

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Numbers.Length);
        Assert.AreEqual(0, result.Tags.Length);
    }

    [TestMethod]
    [ExpectedException(typeof(NotSupportedException))]
    public void SerializeUnsupportedTypeThrows()
    {
        using var ms = new MemoryStream();
        _context.Serialize("unsupported string", ms);
    }

    [TestMethod]
    [ExpectedException(typeof(NotSupportedException))]
    public void DeserializeUnsupportedTypeThrows()
    {
        using var ms = new MemoryStream();
        // Write some valid BSON
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            writer.WriteEndDocument();
        }
        ms.Position = 0;

        _context.Deserialize(ms, typeof(string));
    }
}

