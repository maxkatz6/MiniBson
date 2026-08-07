# Developing MiniBson

This document is about the repository. To learn how to use MiniBson, see [README.md](README.md).

## Prerequisites

The `global.json` file selects the .NET 10 SDK. Its `rollForward` setting also lets the build use a newer major SDK. Install the .NET SDK 10 or a later version before you build.

## Repository structure

| Project | Target frameworks | Responsibility |
| --- | --- | --- |
| `MiniBson` | `netstandard2.0`, `net8.0` | BSON reader, writer, and serialization attribute, with no reflection |
| `MiniBson.Generator` | `netstandard2.0` | Roslyn incremental source generator. It must target `netstandard2.0` to work as an analyzer |
| `MiniBson.Tests` | `net10.0` | MSTest tests for the runtime, the generator, the diagnostics, the wire format, and compatibility with other BSON tools |

The implementation has two layers:

1. `BsonReader`, `BsonWriter`, and `BsonSize` know the BSON wire format. They know nothing about models. The `BsonType` and `BsonBinarySubType` enums are in `BsonTypes.cs`, because the reader and the writer both use them. `BsonSizeTable` is the one runtime type that only generated code uses.
2. The generator writes model-specific code. That code calls the low-level API.

Keep the BSON format rules in the runtime layer. A change to the generator must use the reader and writer operations. Do not put format rules in the generator a second time.

## Build and test

Run these commands from the repository root:

```bash
dotnet build
dotnet test
```

The test project sets `EmitCompilerGeneratedFiles` to true. After a Debug build, you can find the generated contexts here:

```text
MiniBson.Tests/obj/Debug/net10.0/generated/MiniBson.Generator/
```

To find the cause of bad generated code, read this output first. It is usually the fastest method.

## Runtime design

### Document lengths and the seek operation

A BSON document starts with its total length. The writer does not know that length until the document is complete. There are only three solutions to this problem, and MiniBson uses two of them:

1. **Write the length later.** `WriteStartDocument()` writes a placeholder. `WriteEndDocument` then does a seek back to that placeholder and writes the correct length. This is the fastest solution, but it needs a stream that can seek.
2. **Compute the length first.** `WriteStartDocument(int)` writes the correct length immediately. This works with all streams. Generated code uses this solution when `RequiresKnownLength` is true.
3. **Keep the full document in memory, then write it.** MiniBson does not do this. The writer memory would then increase with the document length. Solution 2 gives the same result for generated code, and it does not have that cost.

Because MiniBson does not use solution 3, you must supply the length to the low-level API when the stream cannot seek. If you start a document without a length there, the writer throws an exception.

### Reads without a seek

`BsonReader` tracks its own position rather than using `Stream.Position`. This lets it enforce document boundaries and work with streams that cannot seek.

Skipping can use a forward seek when the stream supports it. Otherwise, the reader consumes and discards the bytes. Preserve both paths because generated deserializers skip elements that they do not know.

### The read window

All reads use one window over the input. A reader constructed from memory points the window at the caller's memory; a reader constructed from a stream rents and refills a window.

Preserve these invariants when changing the reader:

- The reader must not consume bytes past the outermost open document. A stream can contain another document or a peer may be waiting for a response.
- Values and element names can cross refill boundaries or be larger than the window.
- A memory-backed reader must stay within the caller's slice, not the bounds of its underlying array.

Keep byte acquisition centralized. A new source, such as a `ReadOnlySequence<byte>` or a `PipeReader`, should change the refill path rather than every read method.

### The writer buffer

`BsonWriter` uses a fixed-size buffer, so its memory does not grow with the document. Large values go directly to the stream. A completed top-level document must be visible to the caller, while an open document may remain buffered.

Keep output centralized through the buffer and drain path. A new destination, such as an `IBufferWriter<byte>` or a `PipeWriter`, should change that path rather than every write method. Preserve tests for length placeholders both before and after the buffer has drained.

### Length computation

`BsonSize` holds the encoded length of each BSON value. Each of its members agrees with one `BsonWriter` method, and you must change the two types together. `BsonSizeTests` compares each helper with the bytes that the writer wrote. That test is necessary, because you can see a wrong length only as a count of bytes.

For a destination that cannot seek, generated code measures every nested document before writing. `BsonSizeTable` carries those lengths from the measure pass to the write pass without repeatedly measuring nested object graphs. A seekable destination skips the measure pass and lets the writer patch lengths later.

The measure and write passes read the object graph separately. Mutable or computed properties can therefore make them disagree. `WriteEndDocument` detects this instead of producing a bad document, and generator tests must exercise both seekable and non-seekable destinations.

## Type classification

`Map(TypeRefInfo)` is the single classification point used by all emitters. To add a scalar BSON type, add a `BsonMapping` member, a `Map` case, and equivalent operations on `BsonWriter`, `BsonReader`, and `BsonSize`. Add byte-level and round-trip tests for the new mapping.

### Buffer-backed reads

A reader from a `byte[]` or a `ReadOnlyMemory<byte>` puts its window on the caller's memory instead of a rented window. Thus there is no copy and no refill. `ReadBinaryAsMemory()` can therefore return a slice of the caller's memory, and `ReadBinary()` always returns a copy.

The window is the caller's *slice*. It is not the full array. A `ReadOnlyMemory<byte>` is frequently a view of a larger buffer that holds other documents. A read that used the bounds of the array could make a value from the bytes of an adjacent document instead of an error.

### Numeric conversions

`ReadInt32`, `ReadInt64`, and `ReadDouble` accept all three BSON number types and convert between them. This behavior has a purpose: a different BSON tool can use a different width for the same number.

### The Skip method

`Skip()` must accept every type that `BsonType` names, including deprecated types with no accessor. Generated deserializers use it for unknown elements, so add a `Skip()` case whenever you add a `BsonType` member.

### Format details

- MiniBson writes a `DateTime` as UTC milliseconds after the Unix epoch. It converts a local value to UTC, and it reads an unspecified value as UTC. A value smaller than one millisecond is lost.
- A `Guid` uses binary subtype `0x04`. The byte order is the order from `Guid.ToByteArray()` in .NET, which is different from the RFC 4122 order.
- The old binary subtype `0x02` has a second length prefix. The read code and the skip code must both include it.

A change to these rules can stop other BSON tools from reading the data. Write a byte-level test for each such change.

## Generator design

A partial class with one or more `[BsonSerializable(typeof(T))]` attributes becomes a context with these methods:

```csharp
void Serialize(object input, BsonWriter writer);
object? Deserialize(BsonReader reader, Type type);
int GetSerializedSize(object input);
```

The generated API accepts a reader or a writer. It does not accept a stream. Thus the caller selects the input form and controls the lifetime of the resources.

### Type discovery and dispatch

- The top-level dispatch uses the exact runtime type. There is no dispatch on a base class or an interface.
- A registered model is a valid top-level value.
- The generator finds more models through the properties and writes code for them. Such a model is valid only as a nested document. To use it as a top-level value, register it.
- A model can refer to itself.

### Property discovery and schema evolution

The generator collects the public instance properties that it can read. It starts at the most derived type and continues to the base types. If a name occurs two times, the generator keeps the first property. Thus a derived property replaces the property that it hides.

That order is also the order of the elements on the wire. You must not change it, because a different order gives different bytes.

A deserializer switches on the element names. It skips an element that it does not know, and a property with no element keeps its default value. MiniBson does not enforce required members. Test for required properties in your own code.

The generator makes a class with an object initializer. Thus the class needs a parameterless constructor that the generator can use, and each property needs a public `set` or `init` accessor. The generator makes a record with its positional constructor. A record must not add a property in its body that the constructor does not accept.

### Unsupported models

The generator reports `MINIBSON001` for a property that has no read and write mapping. Such a property is one of these:

- A collection that MiniBson does not support.
- A jagged array or a multidimensional array.
- A `decimal` value.
- A property that the deserializer cannot set.

The generated code also contains a fallback that throws an exception. If you suppress the diagnostic, MiniBson still cannot serialize the model.

The generator ignores a context that is not `partial`, and it gives no diagnostic for this.

### Incremental pipeline

The generator starts with `ForAttributeWithMetadataName`. It then sends records with value equality through the incremental pipeline. Those records are `ContextClassInfo`, `TypeInfo`, `TypeRefInfo`, and `EquatableList<T>`.

Do not put an `ISymbol`, a `SyntaxNode`, or another object of the compilation in that model. These objects compare by reference and hold the compilation in memory, and they stop the incremental cache. An array and an `ImmutableArray<T>` also compare by reference, which is the reason for `EquatableList<T>`. The `LocationInfo` type holds a source location as a value for the same reason.

## Test suite

Each test file has one responsibility:

| File | Focus |
| --- | --- |
| `BsonWriterReaderTests.cs` | Low-level round trips, validation, the `Skip` method, disposal, and zero-copy binary reads |
| `BsonSizeTests.cs` | Each `BsonSize` helper against the bytes that the writer wrote |
| `BsonWriterBufferingTests.cs` | Buffer limits, values longer than the buffer, and a length written after a drain |
| `BsonWriterKnownLengthTests.cs` | Lengths from the caller, streams that cannot seek, and the length test |
| `BsonReaderNonSeekableTests.cs` | Reads and skips without a seek, short reads, and exact document limits |
| `BsonReaderWindowTests.cs` | The limits of the read window: bad lengths, slice bounds, and reads across sequential documents |
| `BsonGeneratorTests.cs` | Generated serialization for objects, records, inheritance, null values, and arrays |
| `BsonGeneratorPrimitiveTests.cs` | Scalar, nullable scalar, and scalar array mappings |
| `BsonGeneratorEnumTests.cs` | Enum underlying types, nullable enums, arrays, and nested enums |
| `BsonGeneratorSizeTests.cs` | Generated `GetSerializedSize` against the bytes that the writer wrote |
| `BsonGeneratorDiagnosticTests.cs` | Roslyn assertions for `MINIBSON001` and for valid generator output |
| `MetsysCrossTests.cs` | Byte-level compatibility with Metsys.Bson |
| `NewtonsoftBsonCrossTests.cs` | Read and write compatibility with `Newtonsoft.Json.Bson` |

`NonSeekableStream.cs`, `DualPathWriter.cs`, and `DualPathReader.cs` are shared helpers. They are not test classes.

`NonSeekableStream` cannot seek and cannot report a position. Its `chunkSize` parameter limits the number of bytes that one `Read` returns. A real network stream returns short reads, and code that expects one call to fill the request is a common error.

`DualPathWriter` serializes a value two times. It uses a length that it writes later, and then a length that it computes first. It then asserts that the two results have the same bytes.

`DualPathReader` deserializes a value two times. It uses a stream that can seek, and then a stream that cannot seek. `BsonGeneratorTests` compares the two results with a second serialization, because most test models are classes without value equality.

The generator test files use both helpers. Thus each model in those files tests all four paths.

When you change the wire format, add a byte-level assertion. A round-trip test is not sufficient. A reader bug and a writer bug that agree can still give a correct round trip. The same rule applies to lengths: only a count of bytes finds a wrong one.

When you add a new model shape, test both directions. Put the test in the file with the nearest category. The diagnostic tests run the generator in the test process. Add a test there for each new rejection.

## Packages

`MiniBson/MiniBson.csproj` makes two packages from the same runtime sources.

First, build the solution in Release. The generator assembly must exist in the configuration that you pack:

```bash
dotnet build -c Release
```

Then make the assembly package:

```bash
dotnet pack MiniBson/MiniBson.csproj -c Release --no-build
```

Or make the source-only package:

```bash
dotnet pack MiniBson/MiniBson.csproj -c Release --no-build -p:MiniBsonPackageAsSource=true
```

### Regular package

The `MiniBson` package contains the runtime assembly. It puts `MiniBson.Generator.dll` in `analyzers/dotnet/cs`. The project defines `MINIBSON_PUBLIC`, so the runtime types are public.

### Source-only package

The `MiniBson.Source` package includes the runtime `.cs` files as `contentFiles`. Its MSBuild files do these operations:

- `build/MiniBson.Source.props` includes the sources. It defines `MINIBSON_PUBLIC` only if the consumer sets `MiniBsonPublic=true`.
- `build/MiniBson.Source.targets` adds the source files to a direct consumer.
- `buildTransitive/MiniBson.Source.targets` is empty. An empty file stops a downstream project from a second copy of the sources.

The package does not include the usual build output or the dependencies, but it does include the generator analyzer.

### Local package cache

After a pack, `UpdateLocalNugetCache` puts the package in the global NuGet cache as version `9999.0.0-localbuild`. This lets a local consumer project use the package without a separate feed. It is not a step in a release.

A pack changes that cache entry. Thus a successful pack alone is not sufficient validation for a release. Examine the contents of the `.nupkg` file. If you change how a package is built, use both packages from a new sample project.

## Release checklist

This repository has no CI system and no automatic version numbers. Thus you must do these steps manually for a release:

1. Update `<Version>` in `MiniBson/MiniBson.csproj`.
2. Build and test with the .NET 10 SDK or a later version.
3. Pack both packages in Release.
4. Examine both `.nupkg` files. Test them from new consumer projects.
5. Publish the packages with the usual NuGet release process.
