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

`BsonReader` must do two things with a position. It must know where the current document ends. It must also move forward across values that it skips. Neither operation needs a seek.

The reader keeps its own `_position` field. This field is a count of the bytes that the reader consumed. Every read operation goes through one primitive that keeps `_position` correct.

`Advance` moves the reader forward. First, it discards the bytes in the window. Then it does a seek if the stream can seek. If the stream cannot seek, it reads the bytes and then discards them.

The seek path is necessary. Generated deserializers skip all elements that they do not know. Without that path, the reader must read all the bytes of a large skipped value.

### The read window

All reads come from a window over the input. The window is 8 KB (`WindowSize`). It is the half-open range `[_start, _end)` of `_buffer`.

The source of the window depends on the constructor:

- A reader from a `byte[]` or a `ReadOnlyMemory<byte>` puts the window on the caller's own memory. This reader never refills the window.
- A reader from a stream rents a window and refills it.

One window for both readers is better than two code paths. With two paths, a read on a slice can use the bounds of the full array. That class of bug is not possible now.

Three rules control the refill:

- **The reader stops at the end of the outermost open document** (`_readLimit`). Without this limit, a reader on a socket can consume the bytes of the next message. It can also stop and wait for a peer that already sent its message and now waits for a reply. `RefillTarget` computes how many bytes the reader can put in the window. If no document is open, the reader takes only the number of bytes that the caller asked for. This is how the reader gets the four-byte length prefix of a document and no more bytes.
- **A value can be longer than the bytes in the window.** `ReadCString` reads from the window if the window contains the terminator, which is the usual condition. If the window does not contain the terminator, `ReadCStringAcrossRefills` collects the bytes across more than one refill. A string with a length prefix reads in place if the window is large enough for it.
- **A value can be longer than the window.** `ReadExact` first takes the bytes in the window. Then it reads the other bytes directly into the caller's array. Thus a large binary value never goes through the window.

`EnsureWithinDocument` is the one method that compares a read against `_readLimit`. Only a bad length can fail this test. If the reader consumed those bytes, it would damage the data that comes after them on the stream. Thus the method throws an exception.

All bytes must come from that one window, and this rule has a purpose. To add a different source, such as a `ReadOnlySequence<byte>` or a `PipeReader`, you change the refill path and write an adapter. You do not change each read method.

### The writer buffer

`BsonWriter` keeps bytes in a buffer of a fixed length (`BufferSize`, 8 KB). One method, `Drain()`, moves those bytes to the stream. The buffer length does not depend on the document. Thus the writer memory does not increase with the document length, and a value that is longer than the buffer goes directly to the stream.

This design has three results:

- **`WriteEndDocument` on the top-level document drains the buffer.** If it did not, a caller could see the buffer from outside. Callers wrote their code before the buffer existed, when each write went directly to the stream. If the writer held a complete document back, that code would fail and give no error. Only an open document stays in the buffer.
- `Flush()` drains the buffer and then flushes the stream. `Drain()` does not flush the stream. A flush each time the buffer becomes full would prevent the caller's own buffer from doing its work. `Dispose` does the same operations as `Flush`.
- The writer finds a length placeholder with its own position counter, not with `Stream.Position`. If the placeholder is still in the buffer, the writer changes it in the buffer. If the writer already drained the placeholder, it does a seek on the stream. `BsonWriterBufferingTests` tests both paths.

All bytes must go through that one `Drain()` method. The reason is the same as the reason for the reader window. To add a different destination, such as an `IBufferWriter<byte>` or a `PipeWriter`, you change that one method and write an adapter. You do not change each write method.

### Length computation

`BsonSize` holds the encoded length of each BSON value. Each of its members agrees with one `BsonWriter` method, and you must change the two types together. `BsonSizeTests` compares each helper with the bytes that the writer wrote. That test is necessary, because you can see a wrong length only as a count of bytes.

The generator writes a `Measure{T}Inner` method for each `Write{T}Inner` method. It also writes a `Measure{T}_{Member}Array` helper for each array member. These names come from `EmitScope.MethodPath`, so two models with the same simple name do not get the same method name.

The generator knows the element names, so the element overhead becomes a literal value. The `BsonSize` members are `const`, so the compiler folds the fixed lengths with it. The measure method bodies use a `checked` block. A length that wrapped would agree with no other length, and the writer would then start a document that it cannot describe.

The measure pass measures each nested document one time. `BsonSizeTable` holds the results. The measure pass keeps a slot for each document when it goes down the object graph, and it fills that slot when it comes back up. The write pass then reads the lengths with `Next()` in the same order.

Both passes read the members in the same order under the same conditions. Thus they always agree. If they do not agree, `Next()` throws an exception instead of a wrong length. A second measurement at each write point would cost O(N·depth). That cost is zero for a flat model, but it is quadratic for a model with many levels that refers to itself.

If the destination can seek, the measure pass does not run. `BsonSizeTable.None` replaces the table. `None` is a shared instance, and its `Next()` returns 0. The writer reads 0 as an unknown length and writes the length later. Thus the write pass uses the same code for both destinations.

`GetSerializedSize` on the context is the same measurement, but it is public. It measures into `BsonSizeTable.None`, because no write pass follows it. There is no equivalent method on the reader. A reader knows the length of its document after it reads the prefix, and a caller who needs that length can read the prefix.

The measure pass and the write pass read the same object graph two times, so they can disagree. If they disagree, `WriteEndDocument` throws an exception instead of a bad document. `DualPathWriter` sends each generator test through both paths and compares the bytes. That test finds a disagreement, which you cannot see on a `MemoryStream`.

## Type classification

`Map(TypeRefInfo)` gives a `BsonMapping` for a model type. Each emitter uses that result to select its code. You must not change the order of the tests in `Map`:

- `byte[]` comes before the general array test.
- Enums come before the `SpecialType` switch, because the `SpecialType` of an enum is `None`.

That order is in one method. It is not in each emitter.

You must also not change the names of the scalar `BsonMapping` members. The emitters build method names from them, so the member `Int32` gives `BsonWriter.WriteInt32`, `BsonReader.ReadInt32`, and `BsonSize.Int32`.

**To add a scalar BSON type, add a `BsonMapping` member, a `Map` case, and the equivalent members on those three types. No emitter needs a change.** A new name for a member breaks the code generation and gives no error. The round-trip tests find the failure, but they do not show the cause.

The `Binary`, `BinaryMemory`, `Array`, `Nested`, and `Unsupported` members have separate code in each emitter, because the read direction and the write direction are different for them:

- An array inside an array is not valid.
- `ReadOnlyMemory<byte>` has no write overload without a name.
- A nested document needs its own length prefix and terminator.

### Buffer-backed reads

A reader from a `byte[]` or a `ReadOnlyMemory<byte>` puts its window on the caller's memory instead of a rented window. Thus there is no copy and no refill. `ReadBinaryAsMemory()` can therefore return a slice of the caller's memory, and `ReadBinary()` always returns a copy.

The window is the caller's *slice*. It is not the full array. A `ReadOnlyMemory<byte>` is frequently a view of a larger buffer that holds other documents. A read that used the bounds of the array could make a value from the bytes of an adjacent document instead of an error.

### Numeric conversions

`ReadInt32`, `ReadInt64`, and `ReadDouble` accept all three BSON number types and convert between them. This behavior has a purpose: a different BSON tool can use a different width for the same number.

Each method is one switch across the permitted types, and the error message is in its default arm. A type test before the switch would give the same list of types a second time, and the two lists could then disagree.

### The Skip method

`Skip()` accepts each type that `BsonType` names, and this includes the deprecated types that no accessor reads. Full coverage is necessary. Generated deserializers skip each element that they do not know. If `Skip()` does not know a type, the result is not one bad element. The reader cannot read the document after that element.

If you add a member to `BsonType`, add a case to `Skip()`. The default arm rejects a byte that names no type, because the length of such a value is unknown.

### Format details

- MiniBson writes a `DateTime` as UTC milliseconds after the Unix epoch. It converts a local value to UTC, and it reads an unspecified value as UTC. A value smaller than one millisecond is lost.
- A `Guid` uses binary subtype `0x04`. The byte order is the order from `Guid.ToByteArray()` in .NET, which is different from the RFC 4122 order.
- The old binary subtype `0x02` has a second length prefix. The read code and the skip code must both include it.
- New targets use the span API behind `NET6_0_OR_GREATER`. The `netstandard2.0` target uses a fallback that allocates memory.
- MiniBson puts an element name on the stack if its UTF-8 form is 256 bytes or less.

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

`GetSafeMethodName` makes the generated member names. It builds an identifier from the *fully qualified* type name.

All helpers for all models go into one partial class. If a name came from the simple name, two models with the name `Order` in different namespaces would give two members with the same name. The result is error CS0111 in a file that the user cannot change.

`EmitScope.MethodPath` has the same purpose. `MemberPath` shows a member to the user in a diagnostic and keeps the simple names, because they are easier to read. Thus you must not build an identifier from `MemberPath`.

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
