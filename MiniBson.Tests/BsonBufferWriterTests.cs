using System.Buffers;
using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// The pooled destination that MiniBson supplies. It is the only <see cref="IBufferWriter{T}"/>
/// that a <c>netstandard2.0</c> caller has. Thus it must follow the contract exactly.
/// </summary>
[TestClass]
public sealed class BsonBufferWriterTests
{
    [TestMethod]
    public void GetSpanWithNoHintGivesANonEmptyBuffer()
    {
        using var writer = new BsonBufferWriter();
        Assert.IsTrue(writer.GetSpan().Length > 0, "A hint of 0 must still give a usable buffer.");
        Assert.IsTrue(writer.GetMemory().Length > 0);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(256)]
    [DataRow(8192)]
    [DataRow(100_000)]
    public void GetSpanHonoursTheHint(int hint)
    {
        using var writer = new BsonBufferWriter(1);
        Assert.IsTrue(writer.GetSpan(hint).Length >= hint);
        Assert.IsTrue(writer.GetMemory(hint).Length >= hint);
    }

    [TestMethod]
    public void WrittenBytesSurviveGrowth()
    {
        using var writer = new BsonBufferWriter(1);

        // More than one growth, with a distinct value at each position.
        var expected = new byte[10_000];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = (byte)(i % 251);

        foreach (var b in expected)
        {
            writer.GetSpan(1)[0] = b;
            writer.Advance(1);
        }

        Assert.AreEqual(expected.Length, writer.WrittenCount);
        CollectionAssert.AreEqual(expected, writer.ToArray());
        CollectionAssert.AreEqual(expected, writer.WrittenSpan.ToArray());
        CollectionAssert.AreEqual(expected, writer.WrittenMemory.ToArray());
    }

    /// <summary>
    /// The reason that the constructor takes a capacity. A caller with the length from
    /// <c>GetSerializedSize</c> pays for one rental and no copy.
    /// </summary>
    [TestMethod]
    public void AnExactCapacityNeverGrows()
    {
        const int size = 5000;
        using var writer = new BsonBufferWriter(size);
        var capacity = writer.Capacity;
        Assert.IsTrue(capacity >= size);

        writer.Advance(writer.GetSpan(size).Length >= size ? size : 0);

        Assert.AreEqual(size, writer.WrittenCount);
        Assert.AreEqual(capacity, writer.Capacity, "The writer rented a second array for a length it was given.");
    }

    [TestMethod]
    public void ClearKeepsTheArrayAndResetsTheCount()
    {
        using var writer = new BsonBufferWriter(4096);
        writer.GetSpan(100);
        writer.Advance(100);

        var capacity = writer.Capacity;
        writer.Clear();

        Assert.AreEqual(0, writer.WrittenCount);
        Assert.AreEqual(0, writer.WrittenSpan.Length);
        Assert.AreEqual(capacity, writer.Capacity, "Clear must not release the array.");
    }

    [TestMethod]
    public void AdvancePastTheBufferThrows()
    {
        using var writer = new BsonBufferWriter(16);
        var available = writer.GetSpan().Length;

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Advance(available + 1));
    }

    [TestMethod]
    public void ANegativeAdvanceThrows()
    {
        using var writer = new BsonBufferWriter();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.Advance(-1));
    }

    [TestMethod]
    public void ANegativeCapacityThrows()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BsonBufferWriter(-1));
    }

    [TestMethod]
    public void ANegativeHintThrows()
    {
        using var writer = new BsonBufferWriter();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer.GetSpan(-1));
    }

    [TestMethod]
    public void DisposeIsIdempotent()
    {
        var writer = new BsonBufferWriter(1024);
        writer.GetSpan(10);
        writer.Advance(10);

        writer.Dispose();
        writer.Dispose();

        Assert.AreEqual(0, writer.WrittenCount);
    }

    [TestMethod]
    public void UseAfterDisposeThrows()
    {
        var writer = new BsonBufferWriter(1024);
        writer.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.GetSpan(1));
    }

    /// <summary>A capacity of zero is legal. GetSpan must still give a buffer that is not empty.</summary>
    [TestMethod]
    public void AZeroCapacityStillWrites()
    {
        using var writer = new BsonBufferWriter(0);
        writer.GetSpan(1)[0] = 42;
        writer.Advance(1);

        CollectionAssert.AreEqual(new byte[] { 42 }, writer.ToArray());
    }
}
