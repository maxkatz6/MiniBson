using MiniBson;

namespace MiniBson.Tests;

// Narrow / unsigned scalars. These previously failed to compile at all: the
// generated reader assigned reader.ReadInt32()/ReadInt64() without a cast.
public class AllScalarTypes
{
    public bool Bool { get; set; }
    public byte Byte { get; set; }
    public sbyte SByte { get; set; }
    public short Short { get; set; }
    public ushort UShort { get; set; }
    public int Int { get; set; }
    public uint UInt { get; set; }
    public long Long { get; set; }
    public ulong ULong { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public Guid Guid { get; set; }
}

public class AllNullableScalarTypes
{
    public bool? Bool { get; set; }
    public byte? Byte { get; set; }
    public sbyte? SByte { get; set; }
    public short? Short { get; set; }
    public ushort? UShort { get; set; }
    public uint? UInt { get; set; }
    public ulong? ULong { get; set; }
    public float? Float { get; set; }
}

// Array elements. The generated reader silently dropped every element for the
// narrow/unsigned types, and the generated writer silently dropped every Guid.
public class AllScalarArrayTypes
{
    public bool[] Bools { get; set; } = [];
    public sbyte[] SBytes { get; set; } = [];
    public short[] Shorts { get; set; } = [];
    public ushort[] UShorts { get; set; } = [];
    public int[] Ints { get; set; } = [];
    public uint[] UInts { get; set; } = [];
    public long[] Longs { get; set; } = [];
    public ulong[] ULongs { get; set; } = [];
    public float[] Floats { get; set; } = [];
    public double[] Doubles { get; set; } = [];
    public Guid[] Guids { get; set; } = [];
    public DateTime[] Dates { get; set; } = [];
}

public class NullableScalarArrayTypes
{
    public bool?[] Bools { get; set; } = [];
    public ushort?[] UShorts { get; set; } = [];
    public uint?[] UInts { get; set; } = [];
    public Guid?[] Guids { get; set; } = [];
}

public record ScalarRecord(byte Byte, ushort UShort, uint UInt, ulong ULong, Guid[] Guids);

[BsonSerializable(typeof(AllScalarTypes))]
[BsonSerializable(typeof(AllNullableScalarTypes))]
[BsonSerializable(typeof(AllScalarArrayTypes))]
[BsonSerializable(typeof(NullableScalarArrayTypes))]
[BsonSerializable(typeof(ScalarRecord))]
public partial class PrimitiveBsonContext;

[TestClass]
public sealed class BsonGeneratorPrimitiveTests
{
    private readonly PrimitiveBsonContext _context = new();

    private byte[] Serialize(object input)
    {
        var bytes = DualPathWriter.Serialize(writer => _context.Serialize(input, writer));
        Assert.AreEqual(bytes.Length, _context.GetSerializedSize(input),
            "GetSerializedSize disagrees with the bytes actually written.");
        return bytes;
    }

    private T? RoundTrip<T>(T input) where T : notnull
    {
        using var reader = new BsonReader(Serialize(input));
        return (T?)_context.Deserialize(reader, typeof(T));
    }

    [TestMethod]
    public void RoundTrip_AllScalars_MaxValues()
    {
        var original = new AllScalarTypes
        {
            Bool = true,
            Byte = byte.MaxValue,
            SByte = sbyte.MaxValue,
            Short = short.MaxValue,
            UShort = ushort.MaxValue,
            Int = int.MaxValue,
            UInt = uint.MaxValue,
            Long = long.MaxValue,
            ULong = ulong.MaxValue,
            Float = float.MaxValue,
            Double = double.MaxValue,
            Guid = Guid.NewGuid()
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Bool, result.Bool);
        Assert.AreEqual(original.Byte, result.Byte);
        Assert.AreEqual(original.SByte, result.SByte);
        Assert.AreEqual(original.Short, result.Short);
        Assert.AreEqual(original.UShort, result.UShort);
        Assert.AreEqual(original.Int, result.Int);
        Assert.AreEqual(original.UInt, result.UInt);
        Assert.AreEqual(original.Long, result.Long);
        Assert.AreEqual(original.ULong, result.ULong);
        Assert.AreEqual(original.Float, result.Float);
        Assert.AreEqual(original.Double, result.Double);
        Assert.AreEqual(original.Guid, result.Guid);
    }

    [TestMethod]
    public void RoundTrip_AllScalars_MinValues()
    {
        var original = new AllScalarTypes
        {
            Byte = byte.MinValue,
            SByte = sbyte.MinValue,
            Short = short.MinValue,
            UShort = ushort.MinValue,
            Int = int.MinValue,
            UInt = uint.MinValue,
            Long = long.MinValue,
            ULong = ulong.MinValue,
            Float = float.MinValue,
            Double = double.MinValue
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.SByte, result.SByte);
        Assert.AreEqual(original.Short, result.Short);
        Assert.AreEqual(original.Int, result.Int);
        Assert.AreEqual(original.Long, result.Long);
        Assert.AreEqual(original.Float, result.Float);
        Assert.AreEqual(original.Double, result.Double);
    }

    [TestMethod]
    public void RoundTrip_AllNullableScalars_WithValues()
    {
        var original = new AllNullableScalarTypes
        {
            Bool = true,
            Byte = byte.MaxValue,
            SByte = sbyte.MinValue,
            Short = short.MinValue,
            UShort = ushort.MaxValue,
            UInt = uint.MaxValue,
            ULong = ulong.MaxValue,
            Float = 1.5f
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Bool, result.Bool);
        Assert.AreEqual(original.Byte, result.Byte);
        Assert.AreEqual(original.SByte, result.SByte);
        Assert.AreEqual(original.Short, result.Short);
        Assert.AreEqual(original.UShort, result.UShort);
        Assert.AreEqual(original.UInt, result.UInt);
        Assert.AreEqual(original.ULong, result.ULong);
        Assert.AreEqual(original.Float, result.Float);
    }

    [TestMethod]
    public void RoundTrip_NullableBool_False_IsNotConfusedWithNull()
    {
        var result = RoundTrip(new AllNullableScalarTypes { Bool = false });

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Bool.HasValue);
        Assert.IsFalse(result.Bool!.Value);
    }

    [TestMethod]
    public void RoundTrip_AllNullableScalars_AllNull()
    {
        var result = RoundTrip(new AllNullableScalarTypes());

        Assert.IsNotNull(result);
        Assert.IsNull(result.Bool);
        Assert.IsNull(result.Byte);
        Assert.IsNull(result.SByte);
        Assert.IsNull(result.Short);
        Assert.IsNull(result.UShort);
        Assert.IsNull(result.UInt);
        Assert.IsNull(result.ULong);
        Assert.IsNull(result.Float);
    }

    [TestMethod]
    public void RoundTrip_AllScalarArrays()
    {
        var original = new AllScalarArrayTypes
        {
            Bools = [true, false],
            SBytes = [sbyte.MinValue, 0, sbyte.MaxValue],
            Shorts = [short.MinValue, 0, short.MaxValue],
            UShorts = [ushort.MinValue, 1, ushort.MaxValue],
            Ints = [int.MinValue, 0, int.MaxValue],
            UInts = [uint.MinValue, 1, uint.MaxValue],
            Longs = [long.MinValue, 0, long.MaxValue],
            ULongs = [ulong.MinValue, 1, ulong.MaxValue],
            Floats = [float.MinValue, 0f, float.MaxValue],
            Doubles = [double.MinValue, 0d, double.MaxValue],
            Guids = [Guid.NewGuid(), Guid.Empty, Guid.NewGuid()],
            Dates = [new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc)]
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Bools, result.Bools);
        CollectionAssert.AreEqual(original.SBytes, result.SBytes);
        CollectionAssert.AreEqual(original.Shorts, result.Shorts);
        CollectionAssert.AreEqual(original.UShorts, result.UShorts);
        CollectionAssert.AreEqual(original.Ints, result.Ints);
        CollectionAssert.AreEqual(original.UInts, result.UInts);
        CollectionAssert.AreEqual(original.Longs, result.Longs);
        CollectionAssert.AreEqual(original.ULongs, result.ULongs);
        CollectionAssert.AreEqual(original.Floats, result.Floats);
        CollectionAssert.AreEqual(original.Doubles, result.Doubles);
        CollectionAssert.AreEqual(original.Guids, result.Guids);
        CollectionAssert.AreEqual(original.Dates, result.Dates);
    }

    [TestMethod]
    public void RoundTrip_NullableScalarArrays()
    {
        var original = new NullableScalarArrayTypes
        {
            Bools = [true, null, false],
            UShorts = [ushort.MaxValue, null, 0],
            UInts = [null, uint.MaxValue],
            Guids = [Guid.NewGuid(), null]
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Bools, result.Bools);
        CollectionAssert.AreEqual(original.UShorts, result.UShorts);
        CollectionAssert.AreEqual(original.UInts, result.UInts);
        CollectionAssert.AreEqual(original.Guids, result.Guids);
    }

    [TestMethod]
    public void RoundTrip_ScalarRecord()
    {
        var original = new ScalarRecord(byte.MaxValue, ushort.MaxValue, uint.MaxValue, ulong.MaxValue,
            [Guid.NewGuid(), Guid.NewGuid()]);

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Byte, result.Byte);
        Assert.AreEqual(original.UShort, result.UShort);
        Assert.AreEqual(original.UInt, result.UInt);
        Assert.AreEqual(original.ULong, result.ULong);
        CollectionAssert.AreEqual(original.Guids, result.Guids);
    }

    [TestMethod]
    public void GuidArray_IsActuallyWrittenToTheWire()
    {
        var data = Serialize(new AllScalarArrayTypes { Guids = [Guid.NewGuid(), Guid.NewGuid()] });

        using var reader = new BsonReader(data);
        reader.ReadStartDocument();

        var elementCount = -1;
        while (reader.Read())
        {
            if (reader.CurrentName != "Guids")
            {
                reader.Skip();
                continue;
            }

            elementCount = 0;
            reader.ReadStartArray();
            while (reader.Read())
            {
                Assert.AreEqual(BsonType.Binary, reader.CurrentType);
                reader.Skip();
                elementCount++;
            }
            reader.ReadEndDocument();
        }

        Assert.AreEqual(2, elementCount);
    }

    [TestMethod]
    public void NarrowArrays_AreActuallyWrittenToTheWire()
    {
        var data = Serialize(new AllScalarArrayTypes
        {
            Shorts = [1, 2, 3],
            UShorts = [1, 2],
            UInts = [1, 2, 3, 4],
            ULongs = [1]
        });

        var counts = new Dictionary<string, int>();
        using var reader = new BsonReader(data);
        reader.ReadStartDocument();
        while (reader.Read())
        {
            if (reader.CurrentType != BsonType.Array)
            {
                reader.Skip();
                continue;
            }

            var name = reader.CurrentName;
            var n = 0;
            reader.ReadStartArray();
            while (reader.Read())
            {
                reader.Skip();
                n++;
            }
            reader.ReadEndDocument();
            counts[name] = n;
        }

        Assert.AreEqual(3, counts["Shorts"]);
        Assert.AreEqual(2, counts["UShorts"]);
        Assert.AreEqual(4, counts["UInts"]);
        Assert.AreEqual(1, counts["ULongs"]);
    }

    [TestMethod]
    public void NarrowScalars_UseCompactWireTypes()
    {
        var data = Serialize(new AllScalarTypes
        {
            Bool = true,
            Byte = 1,
            SByte = 1,
            Short = 1,
            UShort = 1,
            Int = 1,
            UInt = 1,
            Long = 1,
            ULong = 1
        });

        var seen = new Dictionary<string, BsonType>();
        using var reader = new BsonReader(data);
        reader.ReadStartDocument();
        while (reader.Read())
        {
            seen[reader.CurrentName] = reader.CurrentType;
            reader.Skip();
        }

        Assert.AreEqual(BsonType.Boolean, seen["Bool"]);
        Assert.AreEqual(BsonType.Int32, seen["Byte"]);
        Assert.AreEqual(BsonType.Int32, seen["SByte"]);
        Assert.AreEqual(BsonType.Int32, seen["Short"]);
        Assert.AreEqual(BsonType.Int32, seen["UShort"]);
        Assert.AreEqual(BsonType.Int32, seen["Int"]);
        Assert.AreEqual(BsonType.Int64, seen["UInt"]);
        Assert.AreEqual(BsonType.Int64, seen["Long"]);
        Assert.AreEqual(BsonType.Int64, seen["ULong"]);
    }
}
