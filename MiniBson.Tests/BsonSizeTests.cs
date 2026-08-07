using MiniBson;

namespace MiniBson.Tests;

/// <summary>
/// Every size helper is checked against what <see cref="BsonWriter"/> actually emits. A
/// round-trip test would not catch drift: a wrong size is only visible as a byte count.
/// </summary>
[TestClass]
public sealed class BsonSizeTests
{
    private static int WrittenLength(Action<BsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var writer = new BsonWriter(ms, leaveOpen: true))
        {
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }

        return (int)ms.Length;
    }

    [TestMethod]
    public void EmptyDocumentIsDocumentOverhead()
    {
        Assert.AreEqual(BsonSize.DocumentOverhead, WrittenLength(_ => { }));
    }

    [TestMethod]
    public void BooleanMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("flag") + BsonSize.Boolean,
            WrittenLength(w => w.WriteBoolean("flag", true)));
    }

    [TestMethod]
    public void Int32MatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("value") + BsonSize.Int32,
            WrittenLength(w => w.WriteInt32("value", 42)));
    }

    [TestMethod]
    public void Int64MatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("value") + BsonSize.Int64,
            WrittenLength(w => w.WriteInt64("value", 42L)));
    }

    [TestMethod]
    public void DoubleMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("value") + BsonSize.Double,
            WrittenLength(w => w.WriteDouble("value", 1.5)));
    }

    [TestMethod]
    public void DateTimeMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("when") + BsonSize.DateTime,
            WrittenLength(w => w.WriteDateTime("when", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
    }

    [TestMethod]
    public void TimestampMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("ts") + BsonSize.Timestamp,
            WrittenLength(w => w.WriteTimestamp("ts", 1, 2)));
    }

    [TestMethod]
    public void ObjectIdMatchesWriter()
    {
        var id = new byte[12];
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("_id") + BsonSize.ObjectId,
            WrittenLength(w => w.WriteObjectId("_id", id)));
    }

    [TestMethod]
    public void GuidMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("id") + BsonSize.Guid,
            WrittenLength(w => w.WriteGuid("id", Guid.NewGuid())));
    }

    [TestMethod]
    public void NullMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("nothing") + BsonSize.Empty,
            WrittenLength(w => w.WriteNull("nothing")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ascii")]
    [DataRow("naïve")]
    [DataRow("日本語")]
    [DataRow("emoji \U0001F600")]
    public void StringMatchesWriter(string value)
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("text") + BsonSize.String(value),
            WrittenLength(w => w.WriteString("text", value)));
    }

    [TestMethod]
    public void StringOfNullIsEmpty()
    {
        Assert.AreEqual(BsonSize.Empty, BsonSize.String(null));
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("")]
    [DataRow("ünïcödé")]
    public void ElementNameMatchesWriter(string name)
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element(name) + BsonSize.Boolean,
            WrittenLength(w => w.WriteBoolean(name, false)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(64)]
    [DataRow(8192)]
    public void BinaryMatchesWriter(int length)
    {
        var payload = new byte[length];
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("data") + BsonSize.Binary(length),
            WrittenLength(w => w.WriteBinary("data", payload)));
    }

    [TestMethod]
    public void BinaryOldMatchesWriter()
    {
        var payload = new byte[16];
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("data") + BsonSize.BinaryOld(payload.Length),
            WrittenLength(w => w.WriteBinary("data", payload, BsonBinarySubType.BinaryOld)));
    }

    [TestMethod]
    public void RegexMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("re") + BsonSize.Regex("^a.*z$", "i"),
            WrittenLength(w => w.WriteRegex("re", "^a.*z$", "i")));
    }

    [TestMethod]
    public void JavaScriptMatchesWriter()
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("fn") + BsonSize.String("f()"),
            WrittenLength(w => w.WriteJavaScript("fn", "f()")));
    }

    [TestMethod]
    public void NestedDocumentMatchesWriter()
    {
        var innerLength = BsonSize.DocumentOverhead + BsonSize.Element("n") + BsonSize.Int32;

        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("inner") + innerLength,
            WrittenLength(w =>
            {
                w.WriteStartDocument("inner");
                w.WriteInt32("n", 1);
                w.WriteEndDocument();
            }));
    }

    // Array keys are decimal indices, so their cost changes at every digit boundary.
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(99)]
    [DataRow(100)]
    [DataRow(101)]
    [DataRow(1000)]
    public void ArrayOverheadMatchesWriter(int count)
    {
        Assert.AreEqual(
            BsonSize.DocumentOverhead + BsonSize.Element("items")
                + BsonSize.ArrayOverhead(count) + count * BsonSize.Int32,
            WrittenLength(w =>
            {
                w.WriteStartArray("items");
                for (var i = 0; i < count; i++)
                    w.WriteInt32(i);
                w.WriteEndArray();
            }));
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 2)]      // "0"
    [DataRow(10, 20)]    // "0".."9"
    [DataRow(11, 23)]    // + "10"
    [DataRow(100, 290)]  // 10*2 + 90*3
    [DataRow(101, 294)]  // + "100"
    [DataRow(1000, 3890)]
    public void ArrayKeyBytesIsExact(int count, int expected)
    {
        Assert.AreEqual(expected, BsonSize.ArrayKeyBytes(count));
    }

    [TestMethod]
    public void NegativeCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BsonSize.ArrayKeyBytes(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BsonSize.ArrayOverhead(-1));
    }

    // Guards the closed-form loop against overflow in the top digit group.
    [TestMethod]
    public void ArrayKeyBytesHandlesLargeCount()
    {
        var brute = 0;
        for (var i = 0; i < 20000; i++)
            brute += i.ToString().Length + 1;

        Assert.AreEqual(brute, BsonSize.ArrayKeyBytes(20000));
    }

    /// <summary>
    /// The keys alone outgrow an int well before the element count does. Accumulating in an
    /// int wraps to a negative length, which the writer then rejects with a message about the
    /// document being too small.
    /// </summary>
    [TestMethod]
    public void CountWhoseKeysOutgrowAnIntIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BsonSize.ArrayKeyBytes(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => BsonSize.ArrayOverhead(int.MaxValue));

        // Just under the boundary still answers, and answers correctly.
        Assert.IsTrue(BsonSize.ArrayKeyBytes(190_000_000) > 0);
    }
}
