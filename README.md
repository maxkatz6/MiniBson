# MiniBson

MiniBson is a small BSON library for .NET. It has a forward-only reader, a forward-only writer, and source-generated serialization. It uses no reflection at run time. The runtime library works with trimming and Native AOT.

## Features

- Source-generated serialization for your models
- Low-level `BsonReader` and `BsonWriter` APIs
- No reflection at run time
- `netstandard2.0` and `net8.0` targets
- No dependency on `net8.0`, and only `System.Memory` on `netstandard2.0`
- An assembly NuGet package and a source-only NuGet package

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

The context uses a `BsonReader` or a `BsonWriter`. You keep the ownership of the streams and the buffers:

```csharp
var context = new AppBsonContext();
var original = new Person
{
    Name = "Ada",
    Age = 37,
    Tags = ["compiler", "math"]
};

byte[] bson;
using (var stream = new MemoryStream())
{
    using (var writer = new BsonWriter(stream, leaveOpen: true))
    {
        context.Serialize(original, writer);
    }

    bson = stream.ToArray();
}

using var reader = new BsonReader(bson);
var copy = (Person?)context.Deserialize(reader, typeof(Person));
```

Each context has these methods:

```csharp
void Serialize(object input, BsonWriter writer);
object? Deserialize(BsonReader reader, Type type);
int GetSerializedSize(object input);
```

`GetSerializedSize` returns the number of bytes that `Serialize` writes, but it writes no bytes
itself. Use it for these tasks:

- Allocate a buffer before you serialize.
- Put a length in front of a message before you send it.
- Reject a value that is too large, before you encode it.

```csharp
var size = context.GetSerializedSize(person);
if (size > MaxMessageBytes)
    throw new InvalidOperationException($"{size} bytes exceeds the limit.");

WriteFrameHeader(socket, size);
using var writer = new BsonWriter(socket, leaveOpen: true);
context.Serialize(person, writer);
```

The number is exact. It is not an estimate. It is the same number that the writer computes when
the destination cannot seek, and the writer throws an exception if the two numbers disagree.
This is true only if each property returns the same value two times. See
[Streams that cannot seek](#streams-that-cannot-seek).

### Model behavior

- The top-level dispatch uses the exact runtime type. Register each concrete type that you give to `Serialize` or `Deserialize`.
- MiniBson also writes code for the types of your properties. Such a type is not a valid top-level value until you register it.
- MiniBson serializes each public instance property that it can read, and it uses the C# name. It includes an inherited property. If a derived property hides a name, MiniBson uses the derived property.
- A deserializer matches the elements by name. It skips an element that it does not know. A property with no element keeps its default value.
- MiniBson writes an enum as a number. A new name for a member is safe, but a new number for a member changes the wire format.
- MiniBson writes a null reference as BSON Null and reads it back as null. The nullable annotation on the property does not change the wire format.

### Streams that cannot seek

A context can serialize and deserialize over all streams. This includes a stream that cannot
seek.

If the destination cannot seek, the generated code computes the length of each document first.
Thus it writes no length later. If the destination can seek, the code does not do that work.
`BsonWriter` writes each length later instead, which is faster. A deserializer needs no
equivalent test, because `BsonReader` never does a seek backwards.

This behavior has no cost, but it adds one rule for your models: **a property must return the
same value two times**. The measure pass and the write pass read the object graph separately.
If a property gives a different value to each pass, the computed length is wrong. These
properties are examples:

- A computed property that returns a new array each time.
- A property on an object that a different thread changes at the same time.

`WriteEndDocument()` finds the disagreement and throws an `InvalidOperationException`. It
writes no bad document.

This test runs only when the destination cannot seek. Thus such a property can pass with a
`MemoryStream` and fail with a socket. If your models have computed properties, test both
destinations.

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

A property that MiniBson does not support gives the compiler error `MINIBSON001`. The error
points at that property:

```text
error MINIBSON001: MiniBson cannot serialize 'Order.Total': type 'decimal' is not supported
```

A different severity for the diagnostic does not add support. The generated code contains a
fallback that throws a `NotSupportedException`. Thus such a property cannot give an empty value
with no error.

## Low-level reader and writer

Use the low-level API if you need direct control of the BSON document. Use it also if you do
not want model types.

### Write a document

```csharp
using var stream = new MemoryStream();

using (var writer = new BsonWriter(stream, leaveOpen: true))
{
    writer.WriteStartDocument();
    writer.WriteString("name", "Ada");
    writer.WriteInt32("age", 37);
    writer.WriteBoolean("active", true);

    writer.WriteStartArray("tags");
    writer.WriteString("compiler");
    writer.WriteString("math");
    writer.WriteEndArray();

    writer.WriteEndDocument();
}

byte[] bson = stream.ToArray();
```

### Read a document

```csharp
using var reader = new BsonReader(bson);
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

### Supported BSON values

| BSON value | Write API | Read API |
| --- | --- | --- |
| Double | `WriteDouble` | `ReadDouble` |
| String | `WriteString` | `ReadString` |
| Document | `WriteStartDocument`, `WriteEndDocument` | `ReadStartDocument`, `ReadStartNestedDocument`, `ReadEndDocument` |
| Array | `WriteStartArray`, `WriteEndArray` | `ReadStartArray`, `ReadEndArray` |
| Binary | `WriteBinary` | `ReadBinary`, `ReadBinaryAsMemory` |
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

An array is a document on the wire. Thus `ReadEndArray` and `ReadEndDocument` are one method
with two names. Use the name that agrees with your write code.

`Skip()` also accepts each deprecated type in the specification: `Undefined`, `DBPointer`,
`Symbol`, `JavaScriptWithScope`, `Decimal128`, `MinKey`, and `MaxKey`. This is true even when
there is no accessor for the value. Generated deserializers skip each element that they do not
know. Thus a document with one of these types stays readable.

A reader from a `byte[]` or a `ReadOnlyMemory<byte>` uses your memory directly. On that path,
`ReadBinaryAsMemory()` returns a slice of your input and makes no copy. If you need a separate
copy, use `ReadBinary()`.

### Streams and document lengths

A BSON document starts with its total length. The writer does not know that length until the
document is complete. `BsonWriter` has two solutions:

| Destination | How to start a document | Cost |
| --- | --- | --- |
| A stream that can seek | `WriteStartDocument()` | The writer writes a placeholder, then `WriteEndDocument()` writes the correct length |
| Any stream | `WriteStartDocument(length)` | You supply the length, and the writer does not go back |

Only the second form works with a stream that cannot seek, such as a network stream or a pipe
stream. If you start a document there without a length, the writer throws an
`InvalidOperationException`. `WriteStartDocument(string, int)`, `WriteStartArray(string, int)`,
`WriteStartNestedDocument(int)`, and `WriteStartNestedArray(int)` accept a length in the same
manner.

The length is the length of the complete document. It includes the four-byte prefix and the
null byte at the end. `BsonSize` gives you the parts:

```csharp
var length = BsonSize.DocumentOverhead
    + BsonSize.Element("name") + BsonSize.String("Ada")
    + BsonSize.Element("age") + BsonSize.Int32;

using var writer = new BsonWriter(networkStream, leaveOpen: true);
writer.WriteStartDocument(length);
writer.WriteString("name", "Ada");
writer.WriteInt32("age", 37);
writer.WriteEndDocument();
```

If your length does not agree with the bytes that the writer wrote, `WriteEndDocument()` throws
an exception. It writes no bad document. `RequiresKnownLength` tells you if the destination
needs a length from you.

A generated serializer does all of this for you. See
[Streams that cannot seek](#streams-that-cannot-seek).

`BsonReader` works with all streams. It keeps its own position and does not ask the stream. If
the stream cannot seek, a skip reads those bytes and discards them. A reader consumes its own
document and no more bytes. Thus you can read a stream that holds a sequence of documents one
document at a time.

### Buffers

`BsonWriter` keeps bytes in a buffer of a fixed length. It does not write each value directly
to the stream. The buffer length does not increase with the document length, and a value that
is longer than the buffer goes directly to the stream.

`WriteEndDocument()` on the top-level document drains the buffer. Thus a complete document is
always on the destination. If you read a `MemoryStream` after that call, you get the full
document. The writer holds back only an open document, and `Flush()` writes that to the
destination.

`Flush()` also flushes the stream, and `Dispose` does the same. This is important when the
destination has its own buffer. A `BufferedStream`, a `GZipStream`, and a `FileStream` that you
keep open are examples.

`BsonReader` also reads ahead into a window. It never reads past the end of its current
document. Thus a stream that holds a sequence of documents stays readable one document at a
time.

## How to contribute

See [DEVELOPMENT.md](DEVELOPMENT.md) for the repository structure, the design notes, the tests,
the package commands, and the documentation rules.

## Acknowledgments

[Claude](https://www.anthropic.com/claude) helped to make a large part of this project.

## License

MiniBson uses the [MIT License](LICENSE).
