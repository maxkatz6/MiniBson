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

1. `BsonReader`, `BsonWriter`, and `BsonSize` understand the BSON wire format but know nothing about application models. The `BsonType` and `BsonBinarySubType` enums live in `BsonTypes.cs`, since reader and writer share them. `BsonSizeTable` sits beside them as the one runtime type that exists only for generated code.
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

It maintains `_position` itself — a count of bytes consumed — and every read goes through a primitive that keeps it accurate. `Advance` handles forward movement: it drops buffered bytes first, then seeks when the stream can, and otherwise reads and discards. Keeping the seek path matters because generated deserializers skip every unknown field, and a large skipped value would otherwise be copied rather than jumped over.

### The read window

Reads come from a pooled window (`WindowSize`, 8 KB) over the input, held as the half-open range `[_start, _end)` of `_buffer`. A reader constructed from `byte[]` or `ReadOnlyMemory<byte>` points that window at the caller's own storage and never refills; a stream-backed reader rents a window and refills it. Unifying the two removed the parallel buffer-backed and stream paths, and with them a class of bug where a slice-relative read used array-relative bounds.

Three constraints shape the refill:

- **Read-ahead stops at the end of the outermost open document** (`_readLimit`). Without that bound, a reader on a socket would consume bytes belonging to the next message — or block waiting for a peer that has sent its message and is waiting for a reply. `RefillTarget` computes how much of the window may be filled; with no document open it reads exactly what was asked for, which is how a document's own four-byte length prefix is read without overshooting.
- **Values can span refills.** `ReadCString` decodes from the window when the terminator is already in it, which is the common case, and otherwise accumulates across refills in `ReadCStringAcrossRefills`. Length-prefixed strings decode in place when they fit the window.
- **Values can exceed the window.** `ReadExact` takes what the window has and then reads the remainder straight into the caller's array, so a binary payload is never staged through the window.

`EnsureWithinDocument` is the single place a read is checked against `_readLimit`. Only a malformed length gets there, and consuming those bytes would corrupt whatever follows on the stream, so it throws.

Keeping every byte come from that one window is deliberate: an alternative source (`ReadOnlySequence<byte>`, `PipeReader`) becomes a change to the refill path plus an adapter, rather than a change to every read method.

### Writer buffering

`BsonWriter` stages bytes in a fixed-size pooled buffer (`BufferSize`, 8 KB) and moves them to the stream through a single `Drain()`. This is a fixed window, not a per-document buffer — writer memory does not scale with document size, and payloads larger than the window go straight to the stream.

Three consequences worth knowing:

- **Closing a top-level document drains.** Staging is otherwise observable from outside: callers wrote against write-through semantics before the buffer existed, and holding a finished document back breaks them with no error. Only a document still open stays staged.
- `Flush()` drains *and* flushes the stream; `Drain()` does not, because flushing the destination every time the window fills would defeat whatever buffering the caller wrapped it in. `Dispose` does what `Flush` does.
- Back-patching resolves against a logical position counter rather than `Stream.Position`. When a length placeholder is still staged it is patched in the buffer; once drained, patching seeks the stream. Both paths are covered in `BsonWriterBufferingTests`.

Keeping every byte funnelled through that one `Drain()` is deliberate, for the same reason as the reader's window: an alternative destination (`IBufferWriter<byte>`, `PipeWriter`) becomes a change to that method plus an adapter, rather than a change to every write method.

### Size computation

`BsonSize` holds the encoded size of every BSON value and mirrors `BsonWriter` one member at a time. The two must change together — `BsonSizeTests` asserts each helper against bytes the writer actually emitted, because a wrong size is only observable as a byte count.

The generator emits a `Measure{T}Inner` beside each `Write{T}Inner`, plus a `Measure{T}_{Member}Array` helper per array-typed member, named from `EmitScope.MethodPath` so that two models sharing a simple name do not emit it twice. Field names are known at generation time, so element overhead folds to a literal; `BsonSize` members are `const`, so fixed-size values fold with it. Measure bodies accumulate in a `checked` block: a size that wrapped would agree with nothing, and the writer would be told to open a document it cannot describe.

Nested documents are measured once, not once per write site. `BsonSizeTable` carries the results: the measure pass reserves a slot per document on the way down and fills it on the way back up, and the write pass reads them back with `Next()` in the same pre-order. Both passes visit members in the same order under the same conditions, so they agree by construction; `Next()` throws rather than returning a wrong length if they ever stop agreeing. Measuring each nested document again where it is written would be O(N·depth), which is free for flat models and quadratic for deeply self-referencing ones.

On a seekable destination the measure pass does not run at all. `BsonSizeTable.None` stands in — a shared instance whose `Next()` returns 0, which the writer reads as "patch it in later" — so the write pass takes the same code path either way.

`GetSerializedSize` on the generated context is the same measurement exposed publicly, measured into `BsonSizeTable.None` since nothing is going to replay it. There is deliberately no reader-side counterpart: a reader already knows its document's length once it has read the prefix, and no use case has come up that the caller could not serve by reading that prefix itself.

Measure and write are two independent walks of the same object graph, so they can disagree. When they do, `WriteEndDocument` throws instead of emitting a malformed document. `DualPathWriter` routes every generator test through both framing paths and compares bytes, which is what catches divergence — a mismatch is invisible on a `MemoryStream`.

## Type classification

`Map(TypeRefInfo)` resolves a model type to a `BsonMapping`, and every emitter dispatches on that result. The classification order is load-bearing — `byte[]` before arrays in general, enums before the `SpecialType` switch, since an enum's own `SpecialType` is `None` — and exists in that one method rather than being repeated per emitter.

Names of the scalar `BsonMapping` members are also load-bearing: emitters build call names from them, so `Int32` yields `BsonWriter.WriteInt32`, `BsonReader.ReadInt32`, and `BsonSize.Int32`. **Adding a scalar BSON type is a `BsonMapping` member, a `Map` case, and matching members on those three types — no emitter changes.** Renaming a member silently breaks code generation; the round-trip tests catch it but do not explain it.

`Binary`, `BinaryMemory`, `Array`, `Nested`, and `Unsupported` are handled case by case instead, because each direction treats them differently — arrays inside arrays are rejected, `ReadOnlyMemory<byte>` has no name-less write overload, and nested documents need framing.

### Buffer-backed reads

A reader constructed from `byte[]` or `ReadOnlyMemory<byte>` points its read window at the original storage rather than renting one, so there is no copy and no refill. `ReadBinaryAsMemory()` can therefore return a slice that aliases caller-owned memory; `ReadBinary()` always returns a copy.

The window is the caller's *slice*, not the array behind it. A `ReadOnlyMemory<byte>` is very often a view into a larger buffer holding other documents, and a read that resolved against the array's bounds could compose a value out of a neighbour's bytes rather than failing.

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

Generated member names come from `GetSafeMethodName`, which builds an identifier from the *fully qualified* type name. Every helper for every model lands in one partial class, so a name derived from the simple name makes two models called `Order` in different namespaces emit the same members — a CS0111 in a file the user cannot edit. `EmitScope.MethodPath` exists for the same reason: `MemberPath` names a member to the user in a diagnostic and keeps simple names for readability, so identifiers cannot be derived from it.

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
| `BsonReaderWindowTests.cs` | What the read window refuses to read past: corrupt lengths, slice bounds, and read-ahead across sequential documents |
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
