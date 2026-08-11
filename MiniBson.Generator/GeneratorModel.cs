using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MiniBson.Generator;

// The snapshot with value equality that the incremental pipeline caches. Each type here holds
// values and not Roslyn symbols. Thus a compilation that gives the same model stops before the
// generator writes any code. Add a member only if an emitter reads it. A member that no emitter
// reads is still part of the equality test, and it can only make the cache invalid.

/// <summary>
/// A replacement for <see cref="Location"/> with value equality. A real Location in the
/// incremental model keeps a SyntaxTree in memory and stops the cache.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    public static LocationInfo? From(ISymbol symbol) =>
        From(symbol.Locations.Length > 0 ? symbol.Locations[0] : null);

    public static LocationInfo? From(Location? location) =>
        location?.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

internal sealed record ContextClassInfo(
    string Namespace,
    string ClassName,
    string Accessibility,
    EquatableList<TypeInfo> SerializableTypes,
    LocationInfo? Location);

internal sealed record TypeInfo(
    string FullyQualifiedName,
    string Name,
    bool IsRecord,
    EquatableList<PropertyInfo> Properties);

// IsSettable is true when an object initializer can set the property, that is when the property
// has a public set accessor or a public init accessor.
internal sealed record PropertyInfo(
    string Name,
    TypeRefInfo Type,
    LocationInfo? Location,
    bool IsSettable);

internal sealed record TypeRefInfo(
    string FullyQualifiedName,
    SpecialType SpecialType,
    bool IsValueType,
    bool IsNullable,
    SpecialType? EnumUnderlyingType,
    TypeRefInfo? ArrayElementType,
    TypeRefInfo? NullableUnderlyingType,
    TypeInfo? NestedTypeInfo);
