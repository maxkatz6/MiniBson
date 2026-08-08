using MiniBson;

namespace MiniBson.Tests;

// Full matrix of enum underlying types.
public enum SByteEnum : sbyte { Min = sbyte.MinValue, Zero = 0, Max = sbyte.MaxValue }

public enum ByteEnum : byte { Zero = 0, Max = byte.MaxValue }

public enum ShortEnum : short { Min = short.MinValue, Zero = 0, Max = short.MaxValue }

public enum UShortEnum : ushort { Zero = 0, Max = ushort.MaxValue }

public enum IntEnum { Min = int.MinValue, Zero = 0, Max = int.MaxValue }

public enum UIntEnum : uint { Zero = 0, Max = uint.MaxValue }

public enum LongEnum : long { Min = long.MinValue, Zero = 0, Max = long.MaxValue }

public enum ULongEnum : ulong { Zero = 0, Max = ulong.MaxValue }

[Flags]
public enum FlagsEnum { None = 0, A = 1, B = 2, C = 4, All = A | B | C }

public class AllEnumUnderlyingTypes
{
    public SByteEnum SByteValue { get; set; }
    public ByteEnum ByteValue { get; set; }
    public ShortEnum ShortValue { get; set; }
    public UShortEnum UShortValue { get; set; }
    public IntEnum IntValue { get; set; }
    public UIntEnum UIntValue { get; set; }
    public LongEnum LongValue { get; set; }
    public ULongEnum ULongValue { get; set; }
    public FlagsEnum FlagsValue { get; set; }
}

public class AllNullableEnumUnderlyingTypes
{
    public SByteEnum? SByteValue { get; set; }
    public ByteEnum? ByteValue { get; set; }
    public ShortEnum? ShortValue { get; set; }
    public UShortEnum? UShortValue { get; set; }
    public IntEnum? IntValue { get; set; }
    public UIntEnum? UIntValue { get; set; }
    public LongEnum? LongValue { get; set; }
    public ULongEnum? ULongValue { get; set; }
    public FlagsEnum? FlagsValue { get; set; }
}

public class AllEnumArrayTypes
{
    public SByteEnum[] SByteValues { get; set; } = [];
    public ByteEnum[] ByteValues { get; set; } = [];
    public ShortEnum[] ShortValues { get; set; } = [];
    public UShortEnum[] UShortValues { get; set; } = [];
    public IntEnum[] IntValues { get; set; } = [];
    public UIntEnum[] UIntValues { get; set; } = [];
    public LongEnum[] LongValues { get; set; } = [];
    public ULongEnum[] ULongValues { get; set; } = [];
    public FlagsEnum[] FlagsValues { get; set; } = [];
}

public class TypeWithNullableEnumArray
{
    public IntEnum?[] Values { get; set; } = [];
    public LongEnum?[] LongValues { get; set; } = [];
    public IntEnum[]? NullArray { get; set; }
}

public class EnumHolder
{
    public IntEnum Value { get; set; }
    public UIntEnum? NullableValue { get; set; }
    public ULongEnum[] Values { get; set; } = [];
}

public class TypeWithNestedEnumHolder
{
    public string Name { get; set; } = string.Empty;
    public EnumHolder? Holder { get; set; }
    public EnumHolder[] Holders { get; set; } = [];
}

public record EnumRecord(IntEnum Value, UIntEnum? NullableValue, LongEnum[] Values);

// A class-nested enum, to exercise name mangling in the generator.
public class Outer
{
    public enum Inner : ushort { A = 1, B = 65535 }

    public Inner Value { get; set; }
    public Inner[] Values { get; set; } = [];
}

[BsonSerializable(typeof(AllEnumUnderlyingTypes))]
[BsonSerializable(typeof(AllNullableEnumUnderlyingTypes))]
[BsonSerializable(typeof(AllEnumArrayTypes))]
[BsonSerializable(typeof(TypeWithNullableEnumArray))]
[BsonSerializable(typeof(TypeWithNestedEnumHolder))]
[BsonSerializable(typeof(EnumRecord))]
[BsonSerializable(typeof(Outer))]
public partial class EnumBsonContext;

[TestClass]
public sealed class BsonGeneratorEnumTests
{
    private readonly EnumBsonContext _context = new();

    private byte[] Serialize(object input)
    {
        var bytes = BsonTestWriter.Raw(writer => _context.Serialize(input, writer));
        Assert.AreEqual(bytes.Length, _context.GetSerializedSize(input),
            "GetSerializedSize disagrees with the bytes actually written.");
        return bytes;
    }

    private T? Deserialize<T>(byte[] data)
    {
        var reader = new BsonReader(data);
        return (T?)_context.Deserialize(ref reader, typeof(T));
    }

    private T? RoundTrip<T>(T input) where T : notnull => Deserialize<T>(Serialize(input));

    [TestMethod]
    public void RoundTrip_AllUnderlyingTypes_MaxValues()
    {
        var original = new AllEnumUnderlyingTypes
        {
            SByteValue = SByteEnum.Max,
            ByteValue = ByteEnum.Max,
            ShortValue = ShortEnum.Max,
            UShortValue = UShortEnum.Max,
            IntValue = IntEnum.Max,
            UIntValue = UIntEnum.Max,
            LongValue = LongEnum.Max,
            ULongValue = ULongEnum.Max,
            FlagsValue = FlagsEnum.All
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.SByteValue, result.SByteValue);
        Assert.AreEqual(original.ByteValue, result.ByteValue);
        Assert.AreEqual(original.ShortValue, result.ShortValue);
        Assert.AreEqual(original.UShortValue, result.UShortValue);
        Assert.AreEqual(original.IntValue, result.IntValue);
        Assert.AreEqual(original.UIntValue, result.UIntValue);
        Assert.AreEqual(original.LongValue, result.LongValue);
        Assert.AreEqual(original.ULongValue, result.ULongValue);
        Assert.AreEqual(original.FlagsValue, result.FlagsValue);
    }

    [TestMethod]
    public void RoundTrip_AllUnderlyingTypes_MinValues()
    {
        var original = new AllEnumUnderlyingTypes
        {
            SByteValue = SByteEnum.Min,
            ByteValue = ByteEnum.Zero,
            ShortValue = ShortEnum.Min,
            UShortValue = UShortEnum.Zero,
            IntValue = IntEnum.Min,
            UIntValue = UIntEnum.Zero,
            LongValue = LongEnum.Min,
            ULongValue = ULongEnum.Zero,
            FlagsValue = FlagsEnum.None
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.SByteValue, result.SByteValue);
        Assert.AreEqual(original.ShortValue, result.ShortValue);
        Assert.AreEqual(original.IntValue, result.IntValue);
        Assert.AreEqual(original.LongValue, result.LongValue);
        Assert.AreEqual(original.FlagsValue, result.FlagsValue);
    }

    [TestMethod]
    public void RoundTrip_AllNullableUnderlyingTypes_WithValues()
    {
        var original = new AllNullableEnumUnderlyingTypes
        {
            SByteValue = SByteEnum.Min,
            ByteValue = ByteEnum.Max,
            ShortValue = ShortEnum.Min,
            UShortValue = UShortEnum.Max,
            IntValue = IntEnum.Min,
            UIntValue = UIntEnum.Max,
            LongValue = LongEnum.Min,
            ULongValue = ULongEnum.Max,
            FlagsValue = FlagsEnum.All
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.SByteValue, result.SByteValue);
        Assert.AreEqual(original.ByteValue, result.ByteValue);
        Assert.AreEqual(original.ShortValue, result.ShortValue);
        Assert.AreEqual(original.UShortValue, result.UShortValue);
        Assert.AreEqual(original.IntValue, result.IntValue);
        Assert.AreEqual(original.UIntValue, result.UIntValue);
        Assert.AreEqual(original.LongValue, result.LongValue);
        Assert.AreEqual(original.ULongValue, result.ULongValue);
        Assert.AreEqual(original.FlagsValue, result.FlagsValue);
    }

    [TestMethod]
    public void RoundTrip_AllNullableUnderlyingTypes_AllNull()
    {
        var result = RoundTrip(new AllNullableEnumUnderlyingTypes());

        Assert.IsNotNull(result);
        Assert.IsNull(result.SByteValue);
        Assert.IsNull(result.ByteValue);
        Assert.IsNull(result.ShortValue);
        Assert.IsNull(result.UShortValue);
        Assert.IsNull(result.IntValue);
        Assert.IsNull(result.UIntValue);
        Assert.IsNull(result.LongValue);
        Assert.IsNull(result.ULongValue);
        Assert.IsNull(result.FlagsValue);
    }

    [TestMethod]
    public void RoundTrip_AllEnumArrayTypes()
    {
        var original = new AllEnumArrayTypes
        {
            SByteValues = [SByteEnum.Min, SByteEnum.Zero, SByteEnum.Max],
            ByteValues = [ByteEnum.Zero, ByteEnum.Max],
            ShortValues = [ShortEnum.Min, ShortEnum.Max],
            UShortValues = [UShortEnum.Zero, UShortEnum.Max],
            IntValues = [IntEnum.Min, IntEnum.Max],
            UIntValues = [UIntEnum.Zero, UIntEnum.Max],
            LongValues = [LongEnum.Min, LongEnum.Max],
            ULongValues = [ULongEnum.Zero, ULongEnum.Max],
            FlagsValues = [FlagsEnum.None, FlagsEnum.All]
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.SByteValues, result.SByteValues);
        CollectionAssert.AreEqual(original.ByteValues, result.ByteValues);
        CollectionAssert.AreEqual(original.ShortValues, result.ShortValues);
        CollectionAssert.AreEqual(original.UShortValues, result.UShortValues);
        CollectionAssert.AreEqual(original.IntValues, result.IntValues);
        CollectionAssert.AreEqual(original.UIntValues, result.UIntValues);
        CollectionAssert.AreEqual(original.LongValues, result.LongValues);
        CollectionAssert.AreEqual(original.ULongValues, result.ULongValues);
        CollectionAssert.AreEqual(original.FlagsValues, result.FlagsValues);
    }

    [TestMethod]
    public void RoundTrip_NullableEnumArray()
    {
        var original = new TypeWithNullableEnumArray
        {
            Values = [IntEnum.Max, null, IntEnum.Min],
            LongValues = [null, LongEnum.Max],
            NullArray = null
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(original.Values, result.Values);
        CollectionAssert.AreEqual(original.LongValues, result.LongValues);
        Assert.IsNull(result.NullArray);
    }

    [TestMethod]
    public void RoundTrip_EnumsInNestedObject()
    {
        var original = new TypeWithNestedEnumHolder
        {
            Name = "test",
            Holder = new EnumHolder
            {
                Value = IntEnum.Max,
                NullableValue = UIntEnum.Max,
                Values = [ULongEnum.Max, ULongEnum.Zero]
            },
            Holders =
            [
                new EnumHolder { Value = IntEnum.Min, NullableValue = null, Values = [] },
                new EnumHolder { Value = IntEnum.Zero, NullableValue = UIntEnum.Zero, Values = [ULongEnum.Max] }
            ]
        };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Name, result.Name);
        Assert.IsNotNull(result.Holder);
        Assert.AreEqual(original.Holder.Value, result.Holder.Value);
        Assert.AreEqual(original.Holder.NullableValue, result.Holder.NullableValue);
        CollectionAssert.AreEqual(original.Holder.Values, result.Holder.Values);

        Assert.AreEqual(2, result.Holders.Length);
        Assert.AreEqual(IntEnum.Min, result.Holders[0].Value);
        Assert.IsNull(result.Holders[0].NullableValue);
        Assert.AreEqual(UIntEnum.Zero, result.Holders[1].NullableValue);
        CollectionAssert.AreEqual(original.Holders[1].Values, result.Holders[1].Values);
    }

    [TestMethod]
    public void RoundTrip_EnumsInRecord()
    {
        var original = new EnumRecord(IntEnum.Max, UIntEnum.Max, [LongEnum.Min, LongEnum.Max]);

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Value, result.Value);
        Assert.AreEqual(original.NullableValue, result.NullableValue);
        CollectionAssert.AreEqual(original.Values, result.Values);
    }

    [TestMethod]
    public void RoundTrip_ClassNestedEnum()
    {
        var original = new Outer { Value = Outer.Inner.B, Values = [Outer.Inner.A, Outer.Inner.B] };

        var result = RoundTrip(original);

        Assert.IsNotNull(result);
        Assert.AreEqual(original.Value, result.Value);
        CollectionAssert.AreEqual(original.Values, result.Values);
    }

    [TestMethod]
    public void UnsignedEnums_UseNonNegativeWireValues()
    {
        var data = Serialize(new AllEnumUnderlyingTypes
        {
            UIntValue = UIntEnum.Max,
            ULongValue = ULongEnum.Max
        });

        var reader = new BsonReader(data);
        reader.ReadStartDocument();

        while (reader.Read())
        {
            switch (reader.CurrentName)
            {
                case "UIntValue":
                    Assert.AreEqual(BsonType.Int64, reader.CurrentType);
                    Assert.AreEqual((long)uint.MaxValue, reader.ReadInt64());
                    break;
                case "ULongValue":
                    Assert.AreEqual(BsonType.Int64, reader.CurrentType);
                    // ulong.MaxValue does not fit in a BSON int64; it round-trips
                    // through the unchecked two's-complement bit pattern.
                    Assert.AreEqual(unchecked((long)ulong.MaxValue), reader.ReadInt64());
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    [TestMethod]
    public void SmallEnums_UseInt32OnTheWire()
    {
        var data = Serialize(new AllEnumUnderlyingTypes
        {
            SByteValue = SByteEnum.Min,
            ByteValue = ByteEnum.Max,
            ShortValue = ShortEnum.Min,
            UShortValue = UShortEnum.Max,
            IntValue = IntEnum.Min
        });

        var seen = new Dictionary<string, BsonType>();
        var reader = new BsonReader(data);
        reader.ReadStartDocument();
        while (reader.Read())
        {
            seen[reader.CurrentName] = reader.CurrentType;
            reader.Skip();
        }

        Assert.AreEqual(BsonType.Int32, seen["SByteValue"]);
        Assert.AreEqual(BsonType.Int32, seen["ByteValue"]);
        Assert.AreEqual(BsonType.Int32, seen["ShortValue"]);
        Assert.AreEqual(BsonType.Int32, seen["UShortValue"]);
        Assert.AreEqual(BsonType.Int32, seen["IntValue"]);
        Assert.AreEqual(BsonType.Int64, seen["LongValue"]);
    }
}
