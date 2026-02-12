using Microsoft.CodeAnalysis;

namespace MiniBson.Generator;

internal sealed record ContextClassInfo(
    string Namespace,
    string ClassName,
    string Accessibility,
    EquatableList<TypeInfo> SerializableTypes);
    
internal sealed record TypeInfo(
    string FullyQualifiedName,
    string Name,
    bool IsRecord,
    bool IsValueType,
    EquatableList<PropertyInfo> Properties);

internal sealed record PropertyInfo(
    string Name,
    TypeRefInfo Type);

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
