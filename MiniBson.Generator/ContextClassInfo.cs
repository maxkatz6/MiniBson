using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MiniBson.Generator;

/// <summary>
/// Equatable stand-in for <see cref="Location"/>. Storing a real Location in the
/// incremental model would root a SyntaxTree and defeat caching.
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
    bool IsValueType,
    EquatableList<PropertyInfo> Properties);

internal sealed record PropertyInfo(
    string Name,
    TypeRefInfo Type,
    LocationInfo? Location,
    /// <summary>Assignable from an object initializer (has a public set or init accessor).</summary>
    bool IsSettable);

internal sealed record TypeRefInfo(
    string FullyQualifiedName,
    string Name,
    SpecialType SpecialType,
    bool IsValueType,
    bool IsNullable,
    NullableAnnotation NullableAnnotation,
    TypeKind TypeKind,
    SpecialType? EnumUnderlyingType,
    TypeRefInfo? ArrayElementType,
    TypeRefInfo? NullableUnderlyingType,
    TypeInfo? NestedTypeInfo);
