using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// The generated <c>GetSerializedSize</c>. Every other generator suite already asserts it
/// against the bytes actually written for its own models; this covers the API itself.
/// </summary>
[TestClass]
public sealed class BsonGeneratorSizeTests
{
    private readonly TestBsonContext _context = new();

    private int WrittenLength(object value)
    {
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
            _context.Serialize(value, writer);
        return (int)ms.Length;
    }

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
    /// Array element keys are decimal indices, so the size has to track digit boundaries.
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
    /// The size is what the writer would be handed on a non-seekable destination, so a
    /// disagreement would throw there rather than merely being wrong.
    /// </summary>
    [TestMethod]
    public void SizeCanBeUsedToFrameAWriteOnANonSeekableStream()
    {
        var value = new SimpleType { Name = "framed", Age = 1, IsActive = false };
        var size = _context.GetSerializedSize(value);

        using var backing = new MemoryStream();
        using (var nonSeekable = new NonSeekableStream(backing))
        using (var writer = new BsonWriter(nonSeekable, leaveOpen: true))
        {
            _context.Serialize(value, writer);
        }

        Assert.AreEqual(size, backing.ToArray().Length);
    }

    [TestMethod]
    public void UnregisteredTypeThrows()
    {
        Assert.Throws<NotSupportedException>(() => _context.GetSerializedSize("not a model"));
    }
}
