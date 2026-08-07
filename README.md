# MiniBson

MiniBson is a small BSON library for .NET. It combines a forward-only reader and writer with source-generated serialization, without using runtime reflection. The runtime library is designed for trimming and Native AOT.

## Features

- Source-generated serialization for application types
- Low-level `BsonReader` and `BsonWriter` APIs
- No runtime reflection
- `netstandard2.0` and `net8.0` targets
- No runtime dependency on `net8.0`; only `System.Memory` on `netstandard2.0`
- Conventional assembly and source-only NuGet packages

## Installation

Choose the regular package for most applications:

```bash
dotnet add package MiniBson
```

Choose the source-only package to compile MiniBson directly into your assembly:

```bash
dotnet add package MiniBson.Source
```

The source-only package makes MiniBson types `internal` by default, preventing them from leaking into your public API or colliding with another embedded copy. Set `MiniBsonPublic` when public types are required.

Both packages include the source generator.

## Source-generated serialization

Declare a partial context and register each type that can appear as a top-level value:

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

The generated context operates on `BsonReader` and `BsonWriter` instances, leaving ownership of streams and buffers with the caller:

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

Each context exposes:

```csharp
void Serialize(object input, BsonWriter writer);
object? Deserialize(BsonReader reader, Type type);
int GetSerializedSize(object input);
```

`GetSerializedSize` returns the exact byte count `Serialize` would write, without writing
anything. Use it to pre-allocate a buffer, to length-prefix a message before sending it, or
to reject a value that exceeds a size limit before paying to encode it:

```csharp
var size = context.GetSerializedSize(person);
if (size > MaxMessageBytes)
    throw new InvalidOperationException($"{size} bytes exceeds the limit.");

WriteFrameHeader(socket, size);
using var writer = new BsonWriter(socket, leaveOpen: true);
context.Serialize(person, writer);
```

It is exact, not an estimate — it is the same number the writer computes for itself when the
destination cannot be seeked, and the writer throws if the two disagree. That holds as long
as properties return the same value when read twice; see
[Streaming over non-seekable streams](#streaming-over-non-seekable-streams).

### Model behavior

- Top-level dispatch uses the exact runtime type. Register every concrete type passed directly to `Serialize` or `Deserialize`.
- Types referenced by properties are generated recursively, but are not automatically valid top-level values.
- All public, readable instance properties are serialized under their C# names. Inherited properties are included; a derived property wins when a name is hidden.
- Deserialization matches fields by name. Unknown fields are skipped, and missing fields retain their default values.
- Enums are stored by numeric value. Renaming a member is safe; changing its value changes the wire format.
- A null reference is written as BSON Null and read back as null, whether or not the property is annotated nullable. Nullable annotations do not affect the wire format.

### Streaming over non-seekable streams

Generated contexts serialize and deserialize over any stream, including ones that cannot be
seeked. When the destination cannot be seeked, generated code computes each document's
length before writing it, so nothing has to be patched afterwards. Over a seekable stream it
skips that work and lets `BsonWriter` patch lengths in, which is cheaper. Deserialization
needs no such branch: `BsonReader` never seeks backwards.

This costs nothing to use, but it does add one requirement on models: **a property must
return the same value when read twice**. Computing the size and writing the value are
separate passes over the object graph, so a property whose value changes between them — a
computed property returning a fresh array, or an instance mutated concurrently — makes the
computed length wrong. `WriteEndDocument()` detects the disagreement and throws
`InvalidOperationException`; no malformed document is produced.

The check only applies on the non-seekable path, so an unstable property can pass against a
`MemoryStream` and fail against a socket. Test against both if your models have computed
properties.

### Supported model types

| C# type | BSON representation |
| --- | --- |
| `bool` | Boolean |
| `byte`, `sbyte`, `short`, `ushort`, `int` | Int32 |
| `uint`, `long`, `ulong` | Int64 |
| `float`, `double` | Double |
| `string` | String |
| `DateTime` | UTC milliseconds since the Unix epoch |
| `Guid` | Binary, UUID subtype |
| `byte[]`, `ReadOnlyMemory<byte>` | Binary |
| Enums | Int32 or Int64, according to the underlying type |
| One-dimensional arrays of supported values | Array |
| Other classes and records | Nested document |
| Nullable values | Their normal representation or Null |
| References | Their normal representation, or Null when the value is null |

### Model limitations

- Collections such as `List<T>` and `Dictionary<TKey, TValue>` are not supported; use arrays.
- Multidimensional and jagged arrays are not supported.
- `decimal` is not supported because MiniBson has no Decimal128 mapping.
- Non-record classes require an accessible parameterless constructor, and each discovered property must have a public `set` or `init` accessor.
- Records must be purely positional: their constructor must accept every discovered property in generated order.
- A serialization context must be a partial class. A non-partial context is currently ignored without a diagnostic.

Unsupported members produce compiler error `MINIBSON001` at the affected property, for example:

```text
error MINIBSON001: MiniBson cannot serialize 'Order.Total': type 'decimal' is not supported
```

Changing the diagnostic severity does not add support. Generated code contains a runtime `NotSupportedException` fallback so an unsupported member cannot silently produce an empty value.

## Low-level reader and writer

Use the low-level API when you need direct control over the BSON document or do not want model types.

### Writing

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

### Reading

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

`ReadEndDocument()` closes the current reader context for either a document or an array.

### Supported BSON values

| BSON value | Write API | Read API |
| --- | --- | --- |
| Double | `WriteDouble` | `ReadDouble` |
| String | `WriteString` | `ReadString` |
| Document | `WriteStartDocument`, `WriteEndDocument` | `ReadStartDocument`, `ReadStartNestedDocument`, `ReadEndDocument` |
| Array | `WriteStartArray`, `WriteEndArray` | `ReadStartArray`, `ReadEndDocument` |
| Binary | `WriteBinary` | `ReadBinary`, `ReadBinaryAsMemory` |
| ObjectId | `WriteObjectId` | `ReadObjectId` |
| Boolean | `WriteBoolean` | `ReadBoolean` |
| DateTime | `WriteDateTime` | `ReadDateTime` |
| Null | `WriteNull` | Inspect `CurrentType` or use `ReadValue` |
| Regular expression | `WriteRegex` | `ReadRegex` |
| JavaScript | `WriteJavaScript` | `ReadJavaScript` |
| Int32 | `WriteInt32` | `ReadInt32` |
| Timestamp | `WriteTimestamp` | `ReadTimestamp` |
| Int64 | `WriteInt64` | `ReadInt64` |
| UUID | `WriteGuid` | `ReadGuid` |

Readers constructed from `byte[]` or `ReadOnlyMemory<byte>` use the supplied buffer directly. On that path, `ReadBinaryAsMemory()` returns a zero-copy slice that aliases the input; use `ReadBinary()` when an independent copy is needed.

### Streams and document lengths

A BSON document begins with its total length, which is not known until the document is
complete. `BsonWriter` resolves this in one of two ways:

| Destination | How to open a document | Cost |
| --- | --- | --- |
| Seekable stream | `WriteStartDocument()` | A placeholder is written and patched from `WriteEndDocument()` |
| Any stream | `WriteStartDocument(length)` | The caller supplies the length; nothing is revisited |

Only the second form works on a stream that cannot be seeked, such as a network or pipe
stream. Opening a document without a length there throws `InvalidOperationException`.
`WriteStartDocument(string, int)`, `WriteStartArray(string, int)`,
`WriteStartNestedDocument(int)`, and `WriteStartNestedArray(int)` take lengths the same way.

The length is the complete encoded document, including its four-byte prefix and trailing
null. `BsonSize` computes the pieces:

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

If the supplied length does not match what was written, `WriteEndDocument()` throws rather
than emitting a malformed document. `RequiresKnownLength` reports whether the destination
needs lengths supplied.

Generated serializers handle all of this themselves — see
[Streaming over non-seekable streams](#streaming-over-non-seekable-streams).

`BsonReader` works over any stream. It tracks its own position rather than asking the
stream, and skipping a value consumes those bytes when the stream cannot seek. A reader
consumes exactly its document and no more, so a stream holding several documents in
sequence can be read one at a time.

### Buffering

`BsonWriter` stages bytes in a fixed-size internal buffer rather than writing each value
straight through. The buffer does not grow with document size, and payloads larger than it go
directly to the stream.

Closing a top-level document empties the buffer, so a finished document is always on the
destination — reading a `MemoryStream` after `WriteEndDocument()` returns the whole document.
Only a document still open is held back; `Flush()` publishes that.

`Flush()` also flushes the underlying stream, and so does disposing the writer. That matters
when the destination is wrapped in something with a buffer of its own, such as a
`BufferedStream`, `GZipStream`, or `FileStream` the caller is keeping open.

`BsonReader` buffers too, reading ahead into a pooled window. It never reads past the end of
the document it is on, so a stream holding several documents in sequence stays readable one
document at a time.

## Contributing

See [DEVELOPMENT.md](DEVELOPMENT.md) for repository structure, design notes, tests, and packaging commands.

## Acknowledgments

A substantial part of the project was created with assistance from [Claude](https://www.anthropic.com/claude).

## License

MiniBson is available under the [MIT License](LICENSE).
