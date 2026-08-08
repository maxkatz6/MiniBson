# Developing MiniBson

This document is about the repository. To learn how to use MiniBson, see [README.md](README.md).

## Prerequisites

The `global.json` file selects the .NET 10 SDK. Its `rollForward` setting also lets the build use a newer major SDK. Install the .NET SDK 10 or a later version before you build.

## Repository structure

| Project | Target frameworks | Responsibility |
| --- | --- | --- |
| `MiniBson` | `netstandard2.0`, `net8.0` | BSON reader, writer, buffer writer, and serialization attribute, with no reflection |
| `MiniBson.Generator` | `netstandard2.0` | Roslyn incremental source generator. It must target `netstandard2.0` to work as an analyzer |
| `MiniBson.Tests` | `net10.0` | MSTest tests for the runtime, the generator, the diagnostics, the wire format, and compatibility with other BSON tools |

The implementation has two layers:

1. `BsonReader`, `BsonWriter`, and `BsonSize` know the BSON wire format. They know nothing about models. The `BsonType` and `BsonBinarySubType` enums are in `BsonTypes.cs`, because the reader and the writer both use them. The writer takes any `IBufferWriter<byte>` as its destination; `Polyfills/ArrayBufferWriter<T>` supplies that type on `netstandard2.0`, and it knows nothing about BSON. `BsonSizeTable` is the one runtime type that only generated code uses.
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

### Why the writer is a class and the reader is a ref struct

This difference is deliberate. Do not remove it.

The reader **must** be a `ref struct`. Its input is caller memory, and the reader must not copy it or pin it. The ref struct is what makes the compiler keep the reader, and each span that the reader returns, inside the lifetime of that memory.

The writer's destination is an `IBufferWriter<byte>`, which is an interface and thus an object on the heap. A ref struct writer would remove no allocation. It would only add `ref BsonWriter` to every generated method.

### Document lengths

A BSON document starts with its total length, and an `IBufferWriter<byte>` does not return a byte that it gave out. Thus the length must be correct when the document starts. `WriteStartDocument(int)` is the only form.

The one other solution is to keep the full document in memory and write it at the end. MiniBson does not do this, because the writer memory would then grow with the document. A measure pass gives the same result without that cost.

`WriteEndDocument` compares the declared length against the bytes written. This is the one test that finds a document with a wrong size, so it must run in each build configuration. It also runs on each write. The old placeholder path made it run on half of them.

### The read state machine

The reader has one position: `_span` is the current segment, and `_index` is the position in it. For input that is one piece, `_span` is the full input, and the segment code does not run.

Nine private methods are the only members that touch the position: `EnsureWithinLimit`, `MoveNextSegment`, `TryPeekContiguous`, `ReadIntoCore`, `Advance`, `TakeContiguous`, `TakeMemory`, `ReadByteCore`, and `TakeCString`. Each public read method is written one time against them. **No code above that layer may test `_isMultiSegment`.**

Two fields were removed on purpose:

- **There is no position field.** `BytesConsumed` is `_segmentStart + _index`. A move to the next segment adds the length of the old segment to `_segmentStart` and sets `_index` to zero, so the position stays correct by construction. A separate counter would be one more field to keep in step.
- **There is one limit, not two.** `_limit` is the end of the outermost open document, or the end of the input when no document is open. Thus one test in `EnsureWithinLimit` covers the end of the document and the end of the input.

Keep these invariants:

- The reader must not consume bytes past the outermost open document. The input can hold another document after this one.
- A value, an element name, or a length prefix can lie across a segment boundary.
- A reader over memory must stay inside the caller's *slice* and not inside the bounds of the array behind it. A `ReadOnlyMemory<byte>` is often a view of a larger buffer that holds other documents. A read that used the array bounds could build a value from an adjacent document in place of an error.
- A `ReadOnlySequence<byte>` can hold an empty segment at any place, including the first and the last. The end of the input is "`TryGet` returned false" and never "the current span is empty".
- At the end of the outermost document, `_limit` goes back to the end of the input. Without that, the reader cannot read a second document from the same input.

**Scratch buffers:** a pooled buffer is correct when one method rents it and returns it. Do not hold one across method calls, because the reader has no `Dispose` that could return it. A value that leaves the method uses `new byte[]`. A joined binary payload and a name across segments are the two cases.

### The writer output

`BsonWriter` holds a `Memory<byte>` from the destination and a count of the bytes written into it, and commits with `Advance`. This is the model that `Utf8JsonWriter` uses. The contract allows it: the buffer stays valid until the next `GetMemory`, `GetSpan`, or `Advance` call on that destination, and a write into the buffer does not make it invalid. It adds one rule for callers, which the README states. Do not write to the same destination yourself while a document is open.

**Call `Stage(n)` only with an `n` of 12 or fewer.** That is a scalar, or the digits of an array index. It is the one place that asks for adjacent bytes. Each longer value goes through `WriteBytesRaw`, which fills a buffer, commits it, takes another one, and repeats, so it needs no adjacent bytes. Hold this rule in review. It is what lets the writer work with a destination that gives one byte at a time, which `BsonWriterOutputTests` tests with `SegmentedBufferWriter(1)`.

The size hint asks for the bytes that the outermost document still needs. Each length is known, so that number is exact, and a destination that grows by doubling can take its size one time. It is only a hint. A destination that gives fewer bytes still works, and a demand for adjacent bytes would fail with `PipeWriter`.

### Length computation

`BsonSize` holds the encoded length of each BSON value. Each of its members agrees with one `BsonWriter` method, and you must change the two types together. `BsonSizeTests` compares each helper with the bytes that the writer wrote. That test is necessary, because you can see a wrong length only as a count of bytes.

Generated code measures each nested document before it writes that document. `BsonSizeTable` carries those lengths from the measure pass to the write pass, so the code measures a nested object graph one time. The measure pass now runs for every write. Thus `BsonSizeTable` could become a ref struct and save one allocation for each top-level `Serialize`. It is a class today for two reasons: `BsonSizeTable.None` is a `static readonly` field, which a ref struct cannot be, and each `Measure` method would need `ref`.

The measure pass and the write pass read the object graph separately. Thus a mutable or computed property can make them disagree. `WriteEndDocument` finds that case and writes no bad document.

## Type classification

`Map(TypeRefInfo)` is the single classification point used by all emitters. To add a scalar BSON type, add a `BsonMapping` member, a `Map` case, and equivalent operations on `BsonWriter`, `BsonReader`, and `BsonSize`. Add byte-level and round-trip tests for the new mapping.

### Zero-copy reads, by constructor

| Constructor | `ReadBinary` (span) | `ReadBinaryMemory` |
| --- | --- | --- |
| `ReadOnlySpan<byte>` | Slice | **Copy** — a span has no memory behind it to slice |
| `ReadOnlyMemory<byte>` | Slice | Slice |
| `byte[]` | Slice | Slice |
| `ReadOnlySequence<byte>`, one segment | Slice | Slice |
| `ReadOnlySequence<byte>`, value inside one segment | Slice | Slice |
| `ReadOnlySequence<byte>`, value across segments | Copy | Copy |

`ReadBinaryArray` always copies. Use it for a value that must live longer than the input.

The span constructor with no zero-copy `ReadOnlyMemory` is the one feature that version 2.0 removed. The rule for users is in the README: construct the reader from memory, an array, or a sequence when you want memory back.

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
object? Deserialize(ref BsonReader reader, Type type);
int GetSerializedSize(object input);
```

The generated API accepts a reader or a writer. It accepts no stream and no buffer. Thus the caller selects the input form and controls the lifetime of the resources.

The reader goes by `ref`, because it is a ref struct. A copy would read the same bytes a second time. Emit `ref BsonReader`, which needs C# 7.2. **Do not emit `scoped`**, which needs C# 11. Generated code compiles in the consumer's project with the consumer's `LangVersion`, and the source package must not raise the version that it already needs. That version is C# 12 today, because the runtime sources use collection expressions.

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

`BsonTestWriter.cs`, `SequenceFactory.cs`, `SegmentedBufferWriter.cs`, and `ReaderAssert.cs` are shared helpers. They are not test classes.

`BsonTestWriter` writes a document body one time to a throwaway writer to learn its length, and then writes it a second time for real. Thus a test states the bytes that it wants and not the arithmetic, and `WriteEndDocument` tests the measurement. Its `Document`, `Array`, `NestedDocument`, and `NestedArray` extension methods do the same for a nested document or array.

`SequenceFactory` builds a `ReadOnlySequence<byte>` from a chain of real segments. A sequence from one array has one segment and runs none of the reader's segment code. `AllShapes` gives the standard set. `EverySplit` puts a boundary at each offset in turn. `WithEmptySegments` covers the empty segments that a sequence can hold. `BsonReaderSequenceTests` also asserts that this helper produces many segments. A helper that produced one segment would make each test in that file test nothing.

`SegmentedBufferWriter` is a destination that gives exactly `segmentSize` bytes for any size hint. That is the least that the contract allows. An `ArrayBufferWriter` never does this, so it hides the difference.

`ReaderAssert` asserts that a reader operation throws. `Assert.Throws` does not work with the reader, because a lambda cannot capture a ref local.

**Assert each new reader feature over a span, a sequence with one segment, `Chunked(1)`, and `WithEmptySegments`.** `BsonGeneratorTests` already deserializes each value two times, one time from one piece and one time through `Chunked(3)`.

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

For a release that changes the public API, also update the migration section in [README.md](README.md) and give the release a new major version.
