# Agent guidance

To learn how to *use* MiniBson, see [README.md](README.md). This file holds the rules that
the code does not state for itself.

## Build and test

Use the .NET 10 SDK or later; `global.json` rolls forward to a newer major version.

```bash
dotnet build
dotnet test
```

The test project sets `EmitCompilerGeneratedFiles`, so after a Debug build the generated
contexts are under `MiniBson.Tests/obj/Debug/net10.0/generated/MiniBson.Generator/`. Read
that output first when generated code misbehaves; it is usually the fastest route.

Keep BSON wire-format logic in `MiniBson`. Model-specific code belongs in
`MiniBson.Generator`, and it must go through the reader and writer operations rather than
restating format rules.

## Reader invariants

`BsonReader` has one position: `_span` is the current segment and `_index` the offset in it.
`BytesConsumed` is `_segmentStart + _index`, so there is no separate position field.
`_limit` is the end of the innermost open document, or the end of the input when none is
open — one test therefore covers both ends.

- Never consume past the outermost open document. The input may hold another document after
  this one, and at the end of the outermost document `_limit` must return to the end of the
  input, or a second document cannot be read.
- Any value, element name, or length prefix may lie across a segment boundary.
- A reader over memory stays inside the caller's **slice**, never the bounds of the array
  behind it. A `ReadOnlyMemory<byte>` is often a window onto a larger buffer holding other
  documents.
- End of input is "`TryGet` returned false", never "the current span is empty". A sequence
  may hold an empty segment anywhere, including first and last.
- Only the read primitives (`EnsureWithinLimit`, `MoveNextSegment`, `TryPeekContiguous`,
  `ReadIntoCore`, `Advance`, `TakeContiguous`, `TakeMemory`, `ReadByteCore`, `TakeCString`)
  touch the position. **No code above that layer may test `_isMultiSegment`.**
- A pooled scratch buffer must be rented and returned inside one method — the reader has no
  `Dispose` to return it later. A value that leaves the method uses `new byte[]`.

`Skip()` must accept every type `BsonType` names, including the deprecated ones with no
accessor. Generated deserializers skip unknown elements, so a missing case does not cost one
bad element — it costs the rest of the document. Add a case whenever you add a `BsonType`.

## Writer rules

`BsonWriter` holds a `Memory<byte>` from the destination and commits it with `Advance`, the
model `Utf8JsonWriter` uses. Callers must not write to the same destination while a document
is open; the README states this.

**Call `Stage(n)` only with `n` of 12 or fewer** — a scalar, or the digits of an array index.
It is the one place that asks for adjacent bytes. Everything longer goes through
`WriteBytesRaw`, which fills a buffer, commits it and takes another. Hold this rule in
review: it is what lets a destination that hands out one byte at a time work, which
`BsonWriterOutputTests` exercises with `SegmentedBufferWriter(1)`.

A document's length must be correct when the document starts, because an `IBufferWriter<byte>`
never returns a byte it gave out. `WriteEndDocument` compares the declared length against the
bytes written; that check runs in every build configuration and on every write, and it is the
only thing that catches a wrong size.

`BsonSize` and `BsonWriter` change together, member for member — every `BsonSize` member
corresponds to exactly one writer method.

## Generator rules

- Emit `ref BsonReader`, which needs C# 7.2. **Do not emit `scoped`**, which needs C# 11.
  Generated code compiles in the consumer's project with the consumer's `LangVersion`, and
  the source package must not raise the version it already requires (C# 12, because the
  runtime sources use collection expressions).
- The incremental model (`ContextClassInfo`, `TypeInfo`, `TypeRefInfo`, `EquatableList<T>`,
  `LocationInfo`) must hold values, never an `ISymbol`, a `SyntaxNode`, an array or an
  `ImmutableArray<T>` — those compare by reference, hold the compilation in memory and defeat
  the cache. Add a member only if an emitter reads it; an unread member still takes part in
  the equality test and can only invalidate the cache.
- `Map(TypeRefInfo)` is the single classification point. To add a scalar type, add a
  `BsonMapping` member, a `Map` case, and matching members on `BsonWriter`, `BsonReader` and
  `BsonSize`. The `BsonMapping` names build method names, so renaming a member breaks code
  generation silently.
- Property order is the wire order: most derived type first, first declaration of a repeated
  name wins. Changing that order changes the bytes.
- A context that is not `partial` is ignored, with no diagnostic.

## Testing rules

When you change the wire format, add a **byte-level** assertion. A round-trip test is not
enough — a reader bug and a writer bug that agree still round-trip. The same holds for
lengths: only a count of bytes catches a wrong one.

Assert each new reader feature over a span, a single-segment sequence, `Chunked(1)`, and
`WithEmptySegments`. `SequenceFactory` builds chains of real segments (a sequence from one
array has a single segment and runs none of the segment code); `SegmentedBufferWriter` is a
destination that hands out the legal minimum. `ReaderAssert` exists because a lambda cannot
capture a ref local, so `Assert.Throws` does not work with the reader.

Add a diagnostic test for each new rejection — those tests run the generator in-process,
because the test project cannot compile a model that triggers the diagnostic.

## Packaging

Build Release first; the generator assembly must exist in the configuration being packed.

```bash
dotnet build -c Release
dotnet pack MiniBson/MiniBson.csproj -c Release --no-build
dotnet pack MiniBson/MiniBson.csproj -c Release --no-build -p:MiniBsonPackageAsSource=true
```

`MiniBson` ships the runtime assembly plus `MiniBson.Generator.dll` in `analyzers/dotnet/cs`,
and defines `MINIBSON_PUBLIC` so the types are public. `MiniBson.Source` ships the runtime
`.cs` files as `contentFiles`, internal unless the consumer sets `MiniBsonPublic=true`.
`buildTransitive/MiniBson.Source.targets` is intentionally empty — that is what stops a
downstream project from getting a second copy of the sources.

A pack rewrites the `9999.0.0-localbuild` entry in the global NuGet cache, so a successful
pack alone does not validate a release. Inspect the `.nupkg` contents, and if you change how
a package is built, consume both packages from a fresh sample project.

Releases are manual: bump `<Version>` in `MiniBson/MiniBson.csproj`, build and test, pack
both, inspect both, publish. A release that changes the public API needs a new major version.
