# Developing MiniBson

This document covers the repository itself. For installation and usage, see [README.md](README.md).

## Prerequisites

The repository selects the .NET 10 SDK in `global.json` and permits roll-forward to newer major SDKs. Install .NET SDK 10 or later before building.

## Repository structure

| Project | Target frameworks | Responsibility |
| --- | --- | --- |
| `MiniBson` | `netstandard2.0`, `net8.0` | Reflection-free BSON reader, writer, and serialization attribute |
| `MiniBson.Generator` | `netstandard2.0` | Roslyn incremental source generator; analyzer compatibility requires the `netstandard2.0` target |
| `MiniBson.Tests` | `net10.0` | MSTest coverage for the runtime, generator, diagnostics, wire format, and interoperability |

The implementation has two deliberate layers:

1. `BsonReader`, `BsonWriter`, and `BsonSize` understand the BSON wire format but know nothing about application models. The `BsonType` and `BsonBinarySubType` enums live in `BsonTypes.cs`, since reader and writer share them.
2. The generator emits model-specific code that calls those low-level APIs.

Keep BSON encoding rules in the runtime layer. Generator changes should compose reader and writer operations rather than duplicate format logic.

## Build and test

From the repository root:

```bash
dotnet build
dotnet test
```

The test project enables `EmitCompilerGeneratedFiles`. After a Debug build, generated contexts are under:

```text
MiniBson.Tests/obj/Debug/net10.0/generated/MiniBson.Generator/
```

Inspecting generated output is usually the quickest way to diagnose a malformed source.

## Runtime design

### Document lengths and seeking

BSON documents begin with their total length, which is unknown until the document is complete. There are only three ways to resolve that, and MiniBson uses two of them:

1. **Patch it in afterwards.** `WriteStartDocument()` writes a placeholder and `WriteEndDocument` seeks back to fill it in. Cheapest, but requires a seekable stream.
2. **Compute it up front.** `WriteStartDocument(int)` writes the real length immediately. Works on any stream. This is what generated code does when `RequiresKnownLength` is set.
3. **Buffer the whole document, then flush.** Deliberately not implemented: it makes writer memory scale with document size, and precomputation covers the source-generated path without that cost.

Because option 3 is absent, the low-level API on a non-seekable stream must supply lengths; opening a document without one throws.

### Reading without seeking

`BsonReader` needs two things from a position: knowing where the current document ends, and moving forward over skipped values. Neither requires seeking.

It maintains `_position` itself — a count of bytes consumed — and every read goes through a `*Core` wrapper that keeps it accurate. `Advance` handles forward movement: it seeks when the stream can, and otherwise reads into a pooled discard buffer and throws the bytes away. Keeping the seek path matters because generated deserializers skip every unknown field, and a large skipped value would otherwise be copied rather than jumped over.

The reader consumes exactly its document and never reads ahead, so a stream can hold several documents in sequence. That constraint is why `ReadCString` reads one byte at a time on the stream path (`ReadCStringFromStream`): finding the terminator by scanning would need lookahead the reader cannot give back.

Buffered reading — a pooled read window like the one `BsonWriter` uses — was considered and deferred. It is a bigger problem than the writer's: values can span refills, values can exceed the window, and the window has to be bounded by the root document's length or it eats bytes belonging to whatever follows. It is a perf change rather than a correctness one, and keeping it separate kept the non-seekable work reviewable.

### Writer buffering

`BsonWriter` stages bytes in a fixed-size pooled buffer (`BufferSize`, 8 KB) and drains it through a single `Flush()`. This is a fixed window, not a per-document buffer — writer memory does not scale with document size, and payloads larger than the window go straight to the stream.

Two consequences worth knowing:

- Written data is not visible on the destination until `Flush()` or `Dispose()`. Tests that read a `MemoryStream` while the writer is still alive must flush first.
- Back-patching resolves against a logical position counter rather than `Stream.Position`. When a length placeholder is still staged it is patched in the buffer; once flushed, patching seeks the stream. Both paths are covered in `BsonWriterBufferingTests`.

Keeping every byte funnelled through that one `Flush()` is deliberate: an alternative destination (`IBufferWriter<byte>`, `PipeWriter`) becomes a change to that method plus an adapter, rather than a change to every write method.

### Size computation

`BsonSize` holds the encoded size of every BSON value and mirrors `BsonWriter` one member at a time. The two must change together — `BsonSizeTests` asserts each helper against bytes the writer actually emitted, because a wrong size is only observable as a byte count.

The generator emits a `Measure{T}Inner` beside each `Write{T}Inner`, plus a `Measure{T}_{Member}Array` helper per array-typed member (the write and measure emitters derive that name from `EmitScope.MemberPath` independently, so they agree without coordinating). Field names are known at generation time, so element overhead folds to a literal; `BsonSize` members are `const`, so fixed-size values fold with it.

Nested documents are measured on demand at each write site rather than memoized, which is O(N·depth). That is free for flat models and acceptable at typical nesting, but it does repeat work on deeply self-referencing graphs. A pre-order size cursor filled by the measure pass and consumed by the write pass would make it O(N); generated code is internal, so that can be retrofitted without an API change.

`GetSerializedSize` on the generated context is the same measurement exposed publicly, so it costs nothing beyond the dispatch. There is deliberately no reader-side counterpart: a reader already knows its document's length once it has read the prefix, and no use case has come up that the caller could not serve by reading that prefix itself.

Measure and write are two independent walks of the same object graph, so they can disagree. When they do, `WriteEndDocument` throws instead of emitting a malformed document. `DualPathWriter` routes every generator test through both framing paths and compares bytes, which is what catches divergence — a mismatch is invisible on a `MemoryStream`.

## Type classification

`Map(TypeRefInfo)` resolves a model type to a `BsonMapping`, and every emitter dispatches on that result. The classification order is load-bearing — `byte[]` before arrays in general, enums before the `SpecialType` switch, since an enum's own `SpecialType` is `None` — and exists in that one method rather than being repeated per emitter.

Names of the scalar `BsonMapping` members are also load-bearing: emitters build call names from them, so `Int32` yields `BsonWriter.WriteInt32`, `BsonReader.ReadInt32`, and `BsonSize.Int32`. **Adding a scalar BSON type is a `BsonMapping` member, a `Map` case, and matching members on those three types — no emitter changes.** Renaming a member silently breaks code generation; the round-trip tests catch it but do not explain it.

`Binary`, `BinaryMemory`, `Array`, `Nested`, and `Unsupported` are handled case by case instead, because each direction treats them differently — arrays inside arrays are rejected, `ReadOnlyMemory<byte>` has no name-less write overload, and nested documents need framing.

### Buffer-backed reads

A reader constructed from `byte[]` or `ReadOnlyMemory<byte>` retains the original storage and reads directly from it. This avoids the copying path used for streams. `ReadBinaryAsMemory()` can therefore return a slice that aliases caller-owned memory; `ReadBinary()` always returns a copy.

### Numeric conversions

`ReadInt32`, `ReadInt64`, and `ReadDouble` accept all three BSON numeric representations and convert between them. This leniency is intentional: other BSON implementations may choose a different width for the same logical number.

### Encoding details

- `DateTime` is stored as UTC milliseconds since the Unix epoch. Local values are converted to UTC; unspecified values are treated as UTC. Sub-millisecond precision is lost.
- `Guid` uses binary subtype `0x04` with the byte order returned by .NET's `Guid.ToByteArray()`. This differs from RFC 4122 byte order.
- Legacy binary subtype `0x02` contains a second length prefix. Both reading and skipping must account for it.
- Modern targets use span-based APIs behind `NET6_0_OR_GREATER`; `netstandard2.0` uses allocating fallbacks.
- Field names are stack-allocated when their UTF-8 representation is at most 256 bytes.

Changes to these behaviors can affect interoperability and need byte-level tests.

## Generator design

A partial class with one or more `[BsonSerializable(typeof(T))]` attributes becomes a serialization context with:

```csharp
void Serialize(object input, BsonWriter writer);
object? Deserialize(BsonReader reader, Type type);
```

The generated API accepts readers and writers—not streams—so callers choose the input form and control resource lifetime.

### Type discovery and dispatch

- Top-level dispatch uses exact types. There is no base-class or interface dispatch.
- Registered models are valid top-level values.
- Models reached through properties are generated recursively, but remain nested-only unless explicitly registered.
- Self-referencing models are supported.

### Property discovery and schema evolution

Public readable instance properties are collected from the most-derived type toward its base types. The first occurrence of a name wins, which gives hidden derived properties precedence. This order also determines emitted field order, so changing discovery order changes the serialized byte sequence.

Deserialization switches on field names. Unknown fields are skipped and absent fields keep their defaults; MiniBson does not enforce required members. Perform required-field validation at a higher layer.

Classes are reconstructed with an object initializer and therefore need an accessible parameterless constructor plus public `set` or `init` accessors. Records are reconstructed through their positional constructor and must not add unmatched body properties.

### Unsupported models

The generator reports `MINIBSON001` for members without a read/write mapping, including unsupported collections, jagged or multidimensional arrays, `decimal`, and properties that cannot be assigned during deserialization. Generated code also contains a throwing runtime fallback. Suppressing the diagnostic does not make the model serializable.

A context declaration without `partial` is currently ignored without a diagnostic.

Generated helper names are based on simple type names. Two models with the same simple name can therefore collide even if their namespaces differ.

### Incremental pipeline

The generator begins with `ForAttributeWithMetadataName` and passes value-equal records through the incremental pipeline: `ContextClassInfo`, `TypeInfo`, `TypeRefInfo`, and `EquatableList<T>`.

Do not store `ISymbol`, `SyntaxNode`, or other compilation-owned objects in that model. They compare by reference and retain compilations, defeating incremental caching. Arrays and `ImmutableArray<T>` also compare by reference, which is why `EquatableList<T>` exists. Source locations are captured in the value-based `LocationInfo` type for the same reason.

## Test suite

The test suite is organized by responsibility:

| File | Focus |
| --- | --- |
| `BsonWriterReaderTests.cs` | Low-level round trips, validation, skipping, disposal, and zero-copy binary reads |
| `BsonSizeTests.cs` | Every `BsonSize` helper checked against bytes the writer emitted |
| `BsonWriterBufferingTests.cs` | Staging-buffer boundaries, oversized payloads, and back-patching after a flush |
| `BsonWriterKnownLengthTests.cs` | Caller-supplied lengths, non-seekable streams, and the length-mismatch check |
| `BsonReaderNonSeekableTests.cs` | Reading and skipping without seeking, short reads, and exact document consumption |
| `BsonGeneratorTests.cs` | End-to-end generated serialization for objects, records, inheritance, nullability, and arrays |
| `BsonGeneratorPrimitiveTests.cs` | Scalar, nullable scalar, and scalar-array mappings |
| `BsonGeneratorEnumTests.cs` | Enum underlying types, nullable enums, arrays, and nested enums |
| `BsonGeneratorSizeTests.cs` | Generated `GetSerializedSize` against the bytes actually written |
| `BsonGeneratorDiagnosticTests.cs` | Direct Roslyn assertions for `MINIBSON001` and valid generator output |
| `MetsysCrossTests.cs` | Byte-level compatibility assertions derived from Metsys.Bson |
| `NewtonsoftBsonCrossTests.cs` | Read and write interoperability with `Newtonsoft.Json.Bson` |

`NonSeekableStream.cs`, `DualPathWriter.cs`, and `DualPathReader.cs` are shared helpers rather than test classes.

`NonSeekableStream` refuses to seek or report a position in either direction, and its `chunkSize` caps how much a single `Read` returns — real network streams hand back short reads, and assuming one call fills the request is an easy way to get this wrong.

`DualPathWriter` serializes twice — patched and precomputed — and asserts the results are byte-identical. `DualPathReader` deserializes twice, seekable and not, and `BsonGeneratorTests` compares the two by re-encoding them, since most test models are classes without value equality. The generator suites route through both, so every model they cover validates all four paths.

When changing the wire representation, add a byte-level assertion. A round-trip test is insufficient because matching reader and writer bugs can still round-trip successfully. The same applies to sizes: only a byte count catches a wrong one.

When adding a supported model shape, cover both serialization directions and place the test beside the closest existing category. Diagnostic tests construct the generator in-process and should cover new rejection paths.

## Packaging

`MiniBson/MiniBson.csproj` produces two packages from the same runtime sources.

Build the solution in Release first so the generator assembly exists in the configuration being packed:

```bash
dotnet build -c Release
```

Then create the regular assembly package:

```bash
dotnet pack MiniBson/MiniBson.csproj -c Release --no-build
```

Or create the source-only package:

```bash
dotnet pack MiniBson/MiniBson.csproj -c Release --no-build -p:MiniBsonPackageAsSource=true
```

### Regular package

The `MiniBson` package contains the runtime assembly and places `MiniBson.Generator.dll` under `analyzers/dotnet/cs`. The project defines `MINIBSON_PUBLIC`, so runtime types are public.

### Source-only package

The `MiniBson.Source` package includes the runtime `.cs` files as `contentFiles`. Its MSBuild files behave as follows:

- `build/MiniBson.Source.props` marks source inclusion and defines `MINIBSON_PUBLIC` only when the consumer sets `MiniBsonPublic=true`.
- `build/MiniBson.Source.targets` adds source files to direct consumers.
- `buildTransitive/MiniBson.Source.targets` is intentionally empty, preventing downstream projects from compiling another copy.

The package suppresses normal build output and dependencies, but still includes the generator analyzer.

### Local package cache

After packing, `UpdateLocalNugetCache` expands the package into the global NuGet cache as `9999.0.0-localbuild`. This supports local consumer projects without a separate feed and is not part of publishing a release.

Packing mutates that cache entry, so do not treat a successful pack alone as release validation. Test the produced `.nupkg` contents and, when packaging behavior changes, consume both package variants from a clean sample project.

## Release checklist

There is currently no CI or automated versioning. A release therefore requires manual verification:

1. Update `<Version>` in `MiniBson/MiniBson.csproj`.
2. Build and test with a compatible .NET 10 or later SDK.
3. Pack both package variants in Release.
4. Inspect both `.nupkg` files and test them from clean consumer projects.
5. Publish the intended packages through the normal NuGet release process.
