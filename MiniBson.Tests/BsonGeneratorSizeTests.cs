using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// The generated <c>GetSerializedSize</c> method. Each other generator test file already
/// compares it with the bytes that the writer wrote for its own models. This file tests the
/// method itself.
/// </summary>
[TestClass]
public sealed class BsonGeneratorSizeTests
{
    private readonly TestBsonContext _context = new();

    private int WrittenLength(object value) =>
        BsonTestWriter.Raw(writer => _context.Serialize(value, writer)).Length;

    [TestMethod]
    public void MatchesWrittenLengthForAFlatModel()
    {
        var value = new SimpleType { Name = "Ada", Age = 37, IsActive = true };
        Assert.AreEqual(WrittenLength(value), _context.GetSerializedSize(value));
    }

    [TestMethod]
    public void MatchesWrittenLengthForNestedAndArrayModels()
    {
        var value = new ComplexType
        {
            Id = Guid.NewGuid(),
            Name = "complex",
            Score = 1.5,
            CreatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Items =
            [
                new SimpleType { Name = "one", Age = 1, IsActive = true },
                new SimpleType { Name = "two", Age = 2, IsActive = false },
            ],
            Nested = new NestedType
            {
                Title = "inner",
                Inner = new SimpleType { Name = "deep", Age = 3, IsActive = true },
            },
        };

        Assert.AreEqual(WrittenLength(value), _context.GetSerializedSize(value));
    }

    /// <summary>
    /// The key of an array element is a decimal index. Thus the length must change at each new
    /// digit count.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(100)]
    [DataRow(101)]
    public void MatchesWrittenLengthAcrossArrayIndexWidths(int count)
    {
        var value = new TypeWithArrays
        {
            Numbers = Enumerable.Range(0, count).ToArray(),
            Tags = Enumerable.Range(0, count).Select(i => "tag" + i).ToArray(),
        };

        Assert.AreEqual(WrittenLength(value), _context.GetSerializedSize(value));
    }

    [TestMethod]
    public void MatchesWrittenLengthWithNullsAndMultiByteText()
    {
        var withNulls = new TypeWithNullables();
        Assert.AreEqual(WrittenLength(withNulls), _context.GetSerializedSize(withNulls));

        var withValues = new TypeWithNullables
        {
            NullableString = "日本語 \U0001F600",
            NullableInt = 5,
            NullableDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Assert.AreEqual(WrittenLength(withValues), _context.GetSerializedSize(withValues));
    }

    [TestMethod]
    public void MatchesWrittenLengthForSelfReferencingModels()
    {
        LinkedNode? head = null;
        for (var i = 9; i >= 0; i--)
            head = new LinkedNode { Label = "node" + i, Depth = i, Next = head };

        Assert.AreEqual(WrittenLength(head!), _context.GetSerializedSize(head!));
    }

    /// <summary>
    /// This is the length that the writer computes for itself. Thus a disagreement throws an
    /// exception in <see cref="BsonWriter.WriteEndDocument"/>. It is not only a wrong number.
    /// </summary>
    /// <remarks>
    /// It is also the reason to ask for the size. A destination that takes its capacity from this
    /// number rents one buffer and does not grow.
    /// </remarks>
    [TestMethod]
    public void SizeExactlyPreSizesABufferWriter()
    {
        var value = new SimpleType { Name = "framed", Age = 1, IsActive = false };
        var size = _context.GetSerializedSize(value);

        using var output = new BsonBufferWriter(size);
        var capacity = output.Capacity;

        _context.Serialize(value, new BsonWriter(output));

        Assert.AreEqual(size, output.WrittenCount);
        Assert.AreEqual(capacity, output.Capacity, "The destination grew, so the size was not exact.");
    }

    [TestMethod]
    public void UnregisteredTypeThrows()
    {
        Assert.Throws<NotSupportedException>(() => _context.GetSerializedSize("not a model"));
    }
}
