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

1. `BsonReader` and `BsonWriter` understand the BSON wire format but know nothing about application models.
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

### Seekable streams

BSON documents begin with their total length. `BsonWriter` writes a placeholder, then seeks back from `WriteEndDocument` to patch it. `BsonReader` also changes position when skipping values. Consequently, stream-backed readers and writers require seekable streams.

Buffering complete documents would support non-seekable streams, but would add allocations and is not the current design.

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
| `BsonGeneratorTests.cs` | End-to-end generated serialization for objects, records, inheritance, nullability, and arrays |
| `BsonGeneratorPrimitiveTests.cs` | Scalar, nullable scalar, and scalar-array mappings |
| `BsonGeneratorEnumTests.cs` | Enum underlying types, nullable enums, arrays, and nested enums |
| `BsonGeneratorDiagnosticTests.cs` | Direct Roslyn assertions for `MINIBSON001` and valid generator output |
| `MetsysCrossTests.cs` | Byte-level compatibility assertions derived from Metsys.Bson |
| `NewtonsoftBsonCrossTests.cs` | Read and write interoperability with `Newtonsoft.Json.Bson` |

When changing the wire representation, add a byte-level assertion. A round-trip test is insufficient because matching reader and writer bugs can still round-trip successfully.

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
