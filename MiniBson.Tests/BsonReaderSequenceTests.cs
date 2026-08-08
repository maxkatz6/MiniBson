using System.Buffers;
using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// The reader over input that arrives in pieces, which is what a <c>PipeReader</c> gives.
/// </summary>
/// <remarks>
/// <para>
/// Each value can lie across a segment boundary: a four-byte length prefix, an element name, a
/// string, a binary payload, or the terminator of a document. Each of those takes a different
/// path in the reader than the same value inside one segment. One adjacent buffer runs none of
/// those paths.
/// </para>
/// <para>
/// Thus each document below is read through every shape in
/// <see cref="SequenceFactory.AllShapes"/>. The smaller documents also go through
/// <see cref="SequenceFactory.EverySplit"/>, which puts a boundary at each offset in turn.
/// </para>
/// </remarks>
[TestClass]
public sealed class BsonReaderSequenceTests
{
    /// <summary>
    /// A test of the helper itself. If these shapes had one segment each, every other test in
    /// this file would pass without a boundary and would test nothing.
    /// </summary>
    [TestMethod]
    public void TheFragmentationHarnessReallyProducesManySegments()
    {
        var document = FlatDocument();
        Assert.IsTrue(document.Length > 40, "The corpus document is too small to be worth splitting.");

        static int CountSegments(ReadOnlySequence<byte> sequence)
        {
            var count = 0;
            var position = sequence.Start;
            while (sequence.TryGet(ref position, out _, advance: true))
                count++;
            return count;
        }

        Assert.IsFalse(SequenceFactory.Chunked(document, 1).IsSingleSegment);
        Assert.AreEqual(document.Length, CountSegments(SequenceFactory.Chunked(document, 1)));
        Assert.AreEqual(2, CountSegments(SequenceFactory.SplitAt(document, 5)));

        // The empty segments are present, and they hold no bytes.
        var padded = SequenceFactory.WithEmptySegments(document, 4);
        Assert.AreEqual(document.Length, padded.Length);
        Assert.IsTrue(CountSegments(padded) > CountSegments(SequenceFactory.Chunked(document, 4)));

        // Each shape holds the original bytes. Thus a difference in a test below comes from the
        // reader and not from this helper.
        foreach (var (name, sequence) in SequenceFactory.AllShapes(document))
        {
            CollectionAssert.AreEqual(document, sequence.ToArray(), $"shape {name} changed the bytes.");
        }

        Assert.AreEqual(document.Length + 1, SequenceFactory.EverySplit(document).Count());
    }

    /// <summary>
    /// Runs <paramref name="read"/> over one adjacent span, then over each segmented shape of the
    /// same bytes, and asserts that all results agree.
    /// </summary>
    private static void AssertSameAcrossShapes(byte[] document, ReaderAssert.ReaderFunc<string> read)
    {
        var contiguous = ReadWith(new BsonReader(document), read);

        foreach (var (name, sequence) in SequenceFactory.AllShapes(document))
        {
            var actual = ReadWith(new BsonReader(sequence), read);
            Assert.AreEqual(contiguous, actual, $"Reading from a sequence shaped as {name} gave a different result.");
        }
    }

    private static void AssertSameAtEverySplit(byte[] document, ReaderAssert.ReaderFunc<string> read)
    {
        var contiguous = ReadWith(new BsonReader(document), read);

        var at = 0;
        foreach (var sequence in SequenceFactory.EverySplit(document))
        {
            var actual = ReadWith(new BsonReader(sequence), read);
            Assert.AreEqual(contiguous, actual, $"Reading from a sequence split at byte {at} gave a different result.");
            at++;
        }
    }

    private static string ReadWith(BsonReader reader, ReaderAssert.ReaderFunc<string> read) => read(ref reader);

    // The documents. Each one has a method that reads it back as a string. Thus one assertion
    // covers the values and their order.

    private static byte[] FlatDocument() => BsonTestWriter.Serialize(w =>
    {
        w.WriteInt32("i32", -7);
        w.WriteInt64("i64", long.MinValue);
        w.WriteDouble("dbl", Math.E);
        w.WriteBoolean("b", true);
        w.WriteString("s", "hello");
        w.WriteNull("nil");
    });

    private static string ReadFlat(ref BsonReader r)
    {
        r.ReadStartDocument();
        var text = "";
        while (r.Read())
            text += $"{r.CurrentName}={ReadScalar(ref r)};";
        r.ReadEndDocument();
        return text;
    }

    private static string ReadScalar(ref BsonReader r) => r.CurrentType switch
    {
        BsonType.Int32 => r.ReadInt32().ToString(),
        BsonType.Int64 => r.ReadInt64().ToString(),
        BsonType.Double => r.ReadDouble().ToString("R"),
        BsonType.Boolean => r.ReadBoolean().ToString(),
        BsonType.String => r.ReadString(),
        BsonType.Null => "null",
        _ => throw new InvalidOperationException($"Unexpected {r.CurrentType}")
    };

    [TestMethod]
    public void AFlatDocumentReadsTheSameFromEveryShape() =>
        AssertSameAcrossShapes(FlatDocument(), ReadFlat);

    [TestMethod]
    public void AFlatDocumentReadsTheSameAtEverySplit() =>
        AssertSameAtEverySplit(FlatDocument(), ReadFlat);

    private static byte[] NestedDocument() => BsonTestWriter.Serialize(w =>
    {
        w.WriteInt32("before", 1);
        w.Document("sub", d =>
        {
            d.WriteString("deep", "value");
            d.Document("deeper", x => x.WriteInt32("n", 9));
        });
        w.WriteInt32("after", 2);
    });

    private static string ReadNested(ref BsonReader r)
    {
        r.ReadStartDocument();
        Assert.IsTrue(r.Read());
        var text = $"before={r.ReadInt32()};";

        Assert.IsTrue(r.Read());
        Assert.AreEqual("sub", r.CurrentName);
        r.ReadStartNestedDocument();
        Assert.IsTrue(r.Read());
        text += $"deep={r.ReadString()};";
        Assert.IsTrue(r.Read());
        r.ReadStartNestedDocument();
        Assert.IsTrue(r.Read());
        text += $"n={r.ReadInt32()};";
        r.ReadEndDocument();
        r.ReadEndDocument();

        Assert.IsTrue(r.Read());
        text += $"after={r.ReadInt32()};";
        r.ReadEndDocument();
        return text;
    }

    [TestMethod]
    public void ANestedDocumentReadsTheSameFromEveryShape() =>
        AssertSameAcrossShapes(NestedDocument(), ReadNested);

    [TestMethod]
    public void ANestedDocumentReadsTheSameAtEverySplit() =>
        AssertSameAtEverySplit(NestedDocument(), ReadNested);

    /// <summary>Enough elements that the decimal keys change their digit count two times.</summary>
    private static byte[] ArrayDocument() => BsonTestWriter.Serialize(w =>
        w.Array("items", a =>
        {
            for (var i = 0; i < 105; i++)
                a.WriteInt32(i);
        }));

    private static string ReadArray(ref BsonReader r)
    {
        r.ReadStartDocument();
        Assert.IsTrue(r.Read());
        r.ReadStartArray();

        var text = "";
        while (r.Read())
            text += $"{r.CurrentName}:{r.ReadInt32()},";

        r.ReadEndArray();
        r.ReadEndDocument();
        return text;
    }

    [TestMethod]
    public void AnArrayReadsTheSameFromEveryShape() =>
        AssertSameAcrossShapes(ArrayDocument(), ReadArray);

    /// <summary>A name much longer than a segment. Thus the reader builds it from many pieces.</summary>
    [TestMethod]
    public void ALongNameReadsTheSameFromEveryShape()
    {
        var name = new string('n', 12_000);
        var document = BsonTestWriter.Serialize(w => w.WriteInt32(name, 42));

        AssertSameAcrossShapes(document, (ref BsonReader r) =>
        {
            r.ReadStartDocument();
            Assert.IsTrue(r.Read());
            var result = $"{r.CurrentName.Length}:{r.ReadInt32()}";
            r.ReadEndDocument();
            return result;
        });
    }

    [TestMethod]
    public void ALongStringReadsTheSameFromEveryShape()
    {
        var value = new string('v', 9000);
        var document = BsonTestWriter.Serialize(w => w.WriteString("s", value));

        AssertSameAcrossShapes(document, (ref BsonReader r) =>
        {
            r.ReadStartDocument();
            Assert.IsTrue(r.Read());
            var result = r.ReadString();
            r.ReadEndDocument();
            return result;
        });
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(8191)]
    [DataRow(8192)]
    [DataRow(24576)]
    public void BinaryOfEverySizeReadsTheSameFromEveryShape(int size)
    {
        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        var document = BsonTestWriter.Serialize(w => w.WriteBinary("bin", payload));

        AssertSameAcrossShapes(document, (ref BsonReader r) =>
        {
            r.ReadStartDocument();
            Assert.IsTrue(r.Read());
            var data = r.ReadBinaryArray(out var subType);
            CollectionAssert.AreEqual(payload, data);
            r.ReadEndDocument();
            return $"{subType}:{data.Length}";
        });
    }

    /// <summary>
    /// The reader cannot return a binary value that lies across a boundary as a slice. Thus it
    /// copies the value, and the bytes must still be correct.
    /// </summary>
    [TestMethod]
    public void BinaryAcrossASegmentBoundaryIsCopiedCorrectly()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var document = BsonTestWriter.Serialize(w => w.WriteBinary("bin", payload));

        // This puts a boundary at each offset, which includes offsets inside the payload.
        foreach (var sequence in SequenceFactory.EverySplit(document))
        {
            var reader = new BsonReader(sequence);
            reader.ReadStartDocument();
            Assert.IsTrue(reader.Read());
            CollectionAssert.AreEqual(payload, reader.ReadBinary(out _).ToArray());
            reader.ReadEndDocument();
        }
    }

    /// <summary>An empty document is the shortest input that must still work.</summary>
    [TestMethod]
    public void AnEmptyDocumentReadsTheSameFromEveryShape()
    {
        var document = BsonTestWriter.Serialize(_ => { });

        AssertSameAtEverySplit(document, (ref BsonReader r) =>
        {
            r.ReadStartDocument();
            Assert.IsFalse(r.Read());
            r.ReadEndDocument();
            return $"consumed:{r.BytesConsumed}";
        });
    }

    /// <summary>
    /// Two documents in one sequence. The reader reaches the second one only when the limit goes
    /// back to the full input at the end of the first one.
    /// </summary>
    [TestMethod]
    public void TwoDocumentsInOneSequenceReadOneAfterTheOther()
    {
        var one = BsonTestWriter.Serialize(w => w.WriteInt32("n", 1));
        var two = BsonTestWriter.Serialize(w => w.WriteInt32("n", 2));

        var both = new byte[one.Length + two.Length];
        one.CopyTo(both, 0);
        two.CopyTo(both, one.Length);

        foreach (var (name, sequence) in SequenceFactory.AllShapes(both))
        {
            var reader = new BsonReader(sequence);

            foreach (var expected in new[] { 1, 2 })
            {
                reader.ReadStartDocument();
                Assert.IsTrue(reader.Read());
                Assert.AreEqual(expected, reader.ReadInt32(), $"shape {name}");
                Assert.IsFalse(reader.Read());
                reader.ReadEndDocument();
            }

            Assert.AreEqual(both.Length, reader.BytesConsumed, $"shape {name}");
        }
    }

    /// <summary>
    /// A sequence of only empty segments holds no bytes. The reader must read that as the end of
    /// the input and must not go past it.
    /// </summary>
    [TestMethod]
    public void AnEmptySequenceIsRejected()
    {
        var reader = new BsonReader(SequenceFactory.WithEmptySegments([], 1));

        ReaderAssert.Throws<InvalidDataException>(ref reader, (ref BsonReader r) => r.ReadStartDocument());
    }

    /// <summary>A sequence with one segment must not run the segment code at all.</summary>
    [TestMethod]
    public void ASingleSegmentSequenceGivesMemoryBack()
    {
        var payload = new byte[] { 9, 9, 9, 9 };
        var document = BsonTestWriter.Serialize(w => w.WriteBinary("bin", payload));

        var reader = new BsonReader(new ReadOnlySequence<byte>(document));
        reader.ReadStartDocument();
        Assert.IsTrue(reader.Read());

        var data = reader.ReadBinaryMemory(out _);
        CollectionAssert.AreEqual(payload, data.ToArray());

        Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out var segment));
        Assert.AreSame(document, segment.Array, "A single-segment sequence should slice, not copy.");
    }

    /// <summary>A skip must move the same distance for every shape of the input.</summary>
    [TestMethod]
    public void SkippingReachesTheSameElementFromEveryShape()
    {
        var document = BsonTestWriter.Serialize(w =>
        {
            w.WriteString("skipped", new string('x', 300));
            w.Document("alsoSkipped", d => d.WriteBinary("b", new byte[64]));
            w.Array("arraySkipped", a => a.WriteInt32(1));
            w.WriteInt32("wanted", 4242);
        });

        AssertSameAcrossShapes(document, (ref BsonReader r) =>
        {
            r.ReadStartDocument();
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(r.Read());
                r.Skip();
            }

            Assert.IsTrue(r.Read());
            var result = $"{r.CurrentName}={r.ReadInt32()}";
            Assert.IsFalse(r.Read());
            r.ReadEndDocument();
            return result;
        });
    }
}
