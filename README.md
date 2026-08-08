# MiniBson

MiniBson is a small BSON library for .NET. It has a forward-only reader, a forward-only writer, and source-generated serialization. It uses no reflection at run time. The runtime library works with trimming and Native AOT.

## Features

- Source-generated serialization for your models
- Low-level `BsonReader` and `BsonWriter` APIs
- Writes to any `IBufferWriter<byte>`, and reads from a `ReadOnlySpan<byte>`, a `ReadOnlyMemory<byte>`, or a `ReadOnlySequence<byte>`
- No `Stream` anywhere in the API, so a `PipeWriter` or a `PipeReader` needs no adapter
- No reflection at run time
- `netstandard2.0` and `net8.0` targets
- No dependency on `net8.0`, and only `System.Memory` on `netstandard2.0`
- An assembly NuGet package and a source-only NuGet package

Coming from version 1.x? See [Migrating from 1.x](#migrating-from-1x).

## Installation

For most applications, use the regular package:

```bash
dotnet add package MiniBson
```

To compile MiniBson into your own assembly, use the source-only package:

```bash
dotnet add package MiniBson.Source
```

The source-only package makes the MiniBson types `internal`. Thus they do not become part of your public API, and they do not collide with a second copy in a different assembly. If you need public types, set `MiniBsonPublic` to true.

Both packages include the source generator.

## Source-generated serialization

Declare a partial context. Register each type that can be a top-level value:

```csharp
using MiniBson;

public sealed class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string[] Tags { get; set; } = [];
}

[BsonSerializable(typeof(Person))]
public partial class AppBsonContext
{
}
```

The context uses a `BsonReader` or a `BsonWriter`. You keep the ownership of the buffers:

```csharp
var context = new AppBsonContext();
var original = new Person
{
    Name = "Ada",
    Age = 37,
    Tags = ["compiler", "math"]
};

using var output = new BsonBufferWriter(context.GetSerializedSize(original));
context.Serialize(original, new BsonWriter(output));

var reader = new BsonReader(output.WrittenSpan);
var copy = (Person?)context.Deserialize(ref reader, typeof(Person));
```

`BsonBufferWriter` is the destination that MiniBson supplies. Any `IBufferWriter<byte>` works, including `ArrayBufferWriter<byte>` and `PipeWriter`.

Each context has these methods:

```csharp
void Serialize(object input, BsonWriter writer);
object? Deserialize(ref BsonReader reader, Type type);
int GetSerializedSize(object input);
```

`Deserialize` takes the reader by reference, because `BsonReader` is a ref struct. A copy would read the same bytes a second time.

`GetSerializedSize` returns the number of bytes that `Serialize` writes, but it writes no bytes itself. Use it for these tasks:

- Allocate a buffer before you serialize.
- Put a length in front of a message before you send it.
- Reject a value that is too large, before you encode it.

```csharp
var size = context.GetSerializedSize(person);
if (size > MaxMessageBytes)
    throw new InvalidOperationException($"{size} bytes exceeds the limit.");

WriteFrameHeader(pipe, size);
context.Serialize(person, new BsonWriter(pipe));
```

The number is exact. It is not an estimate. It is the same number that `Serialize` computes for itself, and the writer throws an exception if the two numbers disagree. This is true only if each property returns the same value two times. See [Document lengths](#document-lengths).

Give the number to `BsonBufferWriter`. It then rents one buffer, does not grow, and makes no copy.

### Model behavior

- The top-level dispatch uses the exact runtime type. Register each concrete type that you give to `Serialize` or `Deserialize`.
- MiniBson also writes code for the types of your properties. Such a type is not a valid top-level value until you register it.
- MiniBson serializes each public instance property that it can read, and it uses the C# name. It includes an inherited property. If a derived property hides a name, MiniBson uses the derived property.
- A deserializer matches the elements by name. It skips an element that it does not know. A property with no element keeps its default value.
- MiniBson writes an enum as a number. A new name for a member is safe, but a new number for a member changes the wire format.
- MiniBson writes a null reference as BSON Null and reads it back as null. The nullable annotation on the property does not change the wire format.

### Document lengths

A BSON document starts with its total length, and an `IBufferWriter<byte>` does not return a byte that it gave out. Thus the generated code computes the length of each document before it starts that document. This is the same for every destination.

This costs one more walk of your object graph, and it adds one rule for your models: **a property must return the same value two times**. The measure pass and the write pass read the object graph separately. If a property gives a different value to each pass, the computed length is wrong. These properties are examples:

- A computed property that returns a new array each time.
- A property on an object that a different thread changes at the same time.

`WriteEndDocument()` finds the disagreement and throws an `InvalidOperationException`. It writes no bad document. This test runs on every write. Thus there is no destination where such a property passes without an error.

When the test throws, the destination can already hold some bytes. Discard them. Do not use them. `BsonBufferWriter.Clear()` does this.

### Supported model types

| C# type | BSON representation |
| --- | --- |
| `bool` | Boolean |
| `byte`, `sbyte`, `short`, `ushort`, `int` | Int32 |
| `uint`, `long`, `ulong` | Int64 |
| `float`, `double` | Double |
| `string` | String |
| `DateTime` | UTC milliseconds after the Unix epoch |
| `Guid` | Binary, UUID subtype |
| `byte[]`, `ReadOnlyMemory<byte>` | Binary |
| Enums | Int32 or Int64, according to the underlying type |
| One-dimensional arrays of supported values | Array |
| Other classes and records | Nested document |
| Nullable values | Their usual representation, or Null |
| References | Their usual representation, or Null when the value is null |

### Model limitations

- MiniBson does not support a collection such as `List<T>` or `Dictionary<TKey, TValue>`. Use an array.
- MiniBson does not support a multidimensional array or a jagged array.
- MiniBson does not support `decimal`, because it has no Decimal128 mapping.
- A class that is not a record needs a parameterless constructor that MiniBson can use. Each property also needs a public `set` or `init` accessor.
- A record must be positional. Its constructor must accept each property in the generated order.
- A context must be a partial class. MiniBson ignores a context that is not partial, and it gives no diagnostic.

A property that MiniBson does not support gives the compiler error `MINIBSON001`. The error points at that property:

```text
error MINIBSON001: MiniBson cannot serialize 'Order.Total': type 'decimal' is not supported
```

A different severity for the diagnostic does not add support. The generated code contains a fallback that throws a `NotSupportedException`. Thus such a property cannot give an empty value with no error.

## Low-level reader and writer

Use the low-level API if you need direct control of the BSON document. Use it also if you do not want model types.

### Write a document

Each document needs its length first. `BsonSize` computes it:

```csharp
var tagsLength = BsonSize.ArrayOverhead(2)
    + BsonSize.String("compiler") + BsonSize.String("math");

var length = BsonSize.DocumentOverhead
    + BsonSize.Element("name") + BsonSize.String("Ada")
    + BsonSize.Element("age") + BsonSize.Int32
    + BsonSize.Element("active") + BsonSize.Boolean
    + BsonSize.Element("tags") + tagsLength;

using var output = new BsonBufferWriter(length);
var writer = new BsonWriter(output);

writer.WriteStartDocument(length);
writer.WriteString("name", "Ada");
writer.WriteInt32("age", 37);
writer.WriteBoolean("active", true);

writer.WriteStartArray("tags", tagsLength);
writer.WriteString("compiler");
writer.WriteString("math");
writer.WriteEndArray();

writer.WriteEndDocument();

byte[] bson = output.ToArray();
```

To avoid this arithmetic, use the source generator. See [Source-generated serialization](#source-generated-serialization).

### Read a document

```csharp
var reader = new BsonReader(bson);
reader.ReadStartDocument();

while (reader.Read())
{
    switch (reader.CurrentName)
    {
        case "name":
            Console.WriteLine(reader.ReadString());
            break;
        case "age":
            Console.WriteLine(reader.ReadInt32());
            break;
        case "tags":
            reader.ReadStartArray();
            while (reader.Read())
            {
                Console.WriteLine(reader.ReadString());
            }
            reader.ReadEndDocument();
            break;
        default:
            reader.Skip();
            break;
    }
}

reader.ReadEndDocument();
```

`ReadEndDocument()` closes the current document or the current array.

The reader takes its input in four forms:

```csharp
var reader = new BsonReader(bson);                  // byte[]
var reader = new BsonReader(memory);                // ReadOnlyMemory<byte>
var reader = new BsonReader(span);                  // ReadOnlySpan<byte>
var reader = new BsonReader(sequence);              // ReadOnlySequence<byte>, e.g. from a PipeReader
```

The full document must be in memory. With a `PipeReader`, read the four-byte length first, wait for that number of bytes, and give the reader that slice. `BytesConsumed` gives the end of the document, so you can slice at that point and read the next one.

`BsonReader` is a `ref struct`, the same as `Utf8JsonReader`. It cannot cross an `await`, a lambda cannot capture it, and a class cannot hold it in a field. Pass it as `ref BsonReader`.

### Supported BSON values

| BSON value | Write API | Read API |
| --- | --- | --- |
| Double | `WriteDouble` | `ReadDouble` |
| String | `WriteString` | `ReadString` |
| Document | `WriteStartDocument`, `WriteEndDocument` | `ReadStartDocument`, `ReadStartNestedDocument`, `ReadEndDocument` |
| Array | `WriteStartArray`, `WriteEndArray` | `ReadStartArray`, `ReadEndArray` |
| Binary | `WriteBinary` | `ReadBinary`, `ReadBinaryArray`, `ReadBinaryMemory` |
| ObjectId | `WriteObjectId` | `ReadObjectId` |
| Boolean | `WriteBoolean` | `ReadBoolean` |
| DateTime | `WriteDateTime` | `ReadDateTime` |
| Null | `WriteNull` | Examine `CurrentType`, or use `ReadValue` |
| Regular expression | `WriteRegex` | `ReadRegex` |
| JavaScript | `WriteJavaScript` | `ReadJavaScript` |
| Int32 | `WriteInt32` | `ReadInt32` |
| Timestamp | `WriteTimestamp` | `ReadTimestamp` |
| Int64 | `WriteInt64` | `ReadInt64` |
| UUID | `WriteGuid` | `ReadGuid` |

An array is a document on the wire. Thus `ReadEndArray` and `ReadEndDocument` are one method with two names. Use the name that agrees with your write code.

`Skip()` also accepts each deprecated type in the specification: `Undefined`, `DBPointer`, `Symbol`, `JavaScriptWithScope`, `Decimal128`, `MinKey`, and `MaxKey`. This is true even when there is no accessor for the value. Generated deserializers skip each element that they do not know. Thus a document with one of these types stays readable.

`ReadBinary()` returns a `ReadOnlySpan<byte>` that points into your input. `ReadBinaryMemory()` returns a `ReadOnlyMemory<byte>` slice of it. Neither makes a copy, except in two cases. A reader from a plain `ReadOnlySpan<byte>` has no memory behind it to slice, so `ReadBinaryMemory` copies there. A value that lies across two segments of a sequence also goes into a new array. `ReadBinaryArray()` always copies. Use it when the value must live longer than the input.

### Document lengths and `BsonSize`

A BSON document starts with its total length. An `IBufferWriter<byte>` does not return a byte that it gave out, so the writer cannot write that length later. Thus each document needs its length at the start:

```csharp
writer.WriteStartDocument(length);
```

`WriteStartDocument(string, int)`, `WriteStartArray(string, int)`, `WriteStartNestedDocument(int)`, and `WriteStartNestedArray(int)` take a length in the same manner.

The length is the length of the complete document. It includes the four-byte prefix and the null byte at the end. `BsonSize` gives you the parts. Each helper there agrees with one writer method.

If your length does not agree with the bytes that the writer wrote, `WriteEndDocument()` throws an `InvalidOperationException`. It writes no bad document.

A generated serializer computes all of this for you. See [Document lengths](#document-lengths).

### Output and buffering

`BsonWriter` holds a buffer from the destination and commits it with `Advance`. Two rules follow:

- **Do not write to the same `IBufferWriter<byte>` yourself while a document is open.** The writer holds a buffer from it.
- `WriteEndDocument()` on the top-level document commits each byte. Thus the destination always holds a complete document, and you need no call of your own. Use `Flush()` for a document that you do not finish.

The writer asks the destination for adjacent bytes only for a scalar or the digits of an array index. That is twelve bytes at the most. A longer value fills a buffer, commits it, and takes another one. A destination that gives one byte at a time works.

`BsonBufferWriter` is a pooled destination that can grow. Construct it with the number from `GetSerializedSize`, and it rents one time and does not grow. Its members are `WrittenSpan`, `WrittenMemory`, `Clear()`, and `Dispose()`. A later write makes a span or memory from an earlier call invalid.

## Migrating from 1.x

Version 2.0 removed `Stream` from the API. The wire format did not change, so documents from 1.x read back unchanged.

| 1.x | 2.0 |
| --- | --- |
| `new BsonWriter(stream, leaveOpen)` | `new BsonWriter(bufferWriter)` |
| `new BsonReader(stream, leaveOpen)` | Read the bytes first, then `new BsonReader(bytes)` |
| `writer.WriteStartDocument()` | `writer.WriteStartDocument(length)` — compute it with `BsonSize` |
| `writer.RequiresKnownLength` | Removed. A length is always required. |
| `writer.Dispose()` | Removed. `WriteEndDocument()` commits; `Flush()` commits a partial document. |
| `reader.Dispose()` | Removed. The reader owns nothing. |
| `context.Deserialize(reader, type)` | `context.Deserialize(ref reader, type)` |
| `reader.ReadBinary()` returning a tuple | `reader.ReadBinary(out var subType)` returning a span, or `ReadBinaryArray(out var subType)` |
| `reader.ReadBinaryAsMemory()` | `reader.ReadBinaryMemory(out var subType)` |
| `reader.ReadObjectId()` returning `byte[]` | Returns a `ReadOnlySpan<byte>`. Call `.ToArray()` for the old behavior. |
| `EndOfStreamException` on truncated input | `InvalidDataException` |

To bridge a `Stream` on the write side, write into a `BsonBufferWriter` and copy:

```csharp
using var output = new BsonBufferWriter(context.GetSerializedSize(value));
context.Serialize(value, new BsonWriter(output));
stream.Write(output.WrittenSpan);
```

On the read side, read the stream into a `byte[]` first. `BsonReader` needs the full document in memory, so it does not decode a document in parts. With a `PipeReader`, read the four-byte length, wait for that number of bytes, and pass that slice.

`BsonReader` is now a ref struct. Thus code that held one in a field, or used one across an `await`, needs a new shape. Read the bytes with async code, and then deserialize with sync code.

## How to contribute

See [DEVELOPMENT.md](DEVELOPMENT.md) for the repository structure, the design notes, the tests, the package commands, and the documentation rules.

## Acknowledgments

[Claude](https://www.anthropic.com/claude) helped to make a large part of this project.

## License

MiniBson uses the [MIT License](LICENSE).
