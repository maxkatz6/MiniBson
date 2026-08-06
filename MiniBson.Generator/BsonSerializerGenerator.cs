using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MiniBson.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class BsonSerializerGenerator : IIncrementalGenerator
{
    private const string BsonSerializableAttributeFullName = "MiniBson.BsonSerializableAttribute";

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        id: "MINIBSON001",
        title: "Type is not supported by the MiniBson generator",
        messageFormat: "MiniBson cannot serialize '{0}': {1}",
        category: "MiniBson",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator has no read/write mapping for this type, so it would silently " +
                     "round-trip as an empty value. Change the property type, or exclude it from the " +
                     "serialized model.");

    /// <summary>
    /// A member the emitters could not produce code for.
    /// </summary>
    private sealed record UnsupportedMember(string MemberPath, string Reason, LocationInfo? Location);

    /// <summary>
    /// Collects unsupported members for one generated context class. Write and read
    /// emitters both visit every member, so entries are de-duplicated by path+reason.
    /// </summary>
    private sealed class DiagnosticCollector
    {
        private readonly Dictionary<string, UnsupportedMember> _items = new();

        public bool HasAny => _items.Count > 0;

        public IEnumerable<UnsupportedMember> Items => _items.Values;

        public void Report(UnsupportedMember member) =>
            _items[member.MemberPath + "|" + member.Reason] = member;
    }

    /// <summary>
    /// Identifies the member currently being emitted, so an unsupported type can be
    /// reported against it instead of silently emitting nothing.
    /// </summary>
    private sealed record EmitScope(DiagnosticCollector Diagnostics, string MemberPath, LocationInfo? Location)
    {
        public EmitScope Element() => this with { MemberPath = MemberPath + "[]" };

        /// <summary>
        /// Records a diagnostic and emits a runtime backstop, so the generated code stays
        /// well-formed (no cascading compiler errors) and throws rather than losing data
        /// if the diagnostic is downgraded or suppressed.
        /// </summary>
        public void Unsupported(StringBuilder sb, string indent, string reason, string typeName)
        {
            Diagnostics.Report(new UnsupportedMember(MemberPath, reason, Location));
            sb.AppendLine($"{indent}ThrowUnsupported(\"{MemberPath}\", \"{typeName}\");");
        }

        /// <summary>Reports a type that has no read/write mapping at all.</summary>
        public void UnsupportedType(StringBuilder sb, string indent, TypeRefInfo type)
        {
            var typeName = Display(type);
            Unsupported(sb, indent, $"type '{typeName}' is not supported", typeName);
        }
    }

    private static string Display(TypeRefInfo type) => Display(type.FullyQualifiedName);

    private static string Display(string fullyQualifiedName) =>
        fullyQualifiedName.StartsWith("global::")
            ? fullyQualifiedName.Substring("global::".Length)
            : fullyQualifiedName;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all classes with [BsonSerializable] attribute
        var classDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                BsonSerializableAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetContextClassInfo(ctx, ct))
            .Where(static info => info is not null)
            .Collect();

        context.RegisterSourceOutput(classDeclarations, GenerateCode);
    }

    private static ContextClassInfo? GetContextClassInfo(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol classSymbol)
            return null;

        var classDeclaration = (ClassDeclarationSyntax)context.TargetNode;

        // Check if the class is partial
        if (!classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            return null;

        // Collect all types from [BsonSerializable(typeof(...))] attributes
        var serializableTypes = new List<TypeInfo>();

        foreach (var attribute in classSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != BsonSerializableAttributeFullName)
                continue;

            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is INamedTypeSymbol typeSymbol)
            {
                serializableTypes.Add(ExtractTypeInfo(typeSymbol));
            }
        }

        if (serializableTypes.Count == 0)
            return null;

        return new ContextClassInfo(
            classSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            classSymbol.Name,
            GetAccessibility(classDeclaration),
            new EquatableList<TypeInfo>(serializableTypes),
            LocationInfo.From(classDeclaration.Identifier.GetLocation()));
    }

    private static string GetAccessibility(ClassDeclarationSyntax classDeclaration)
    {
        foreach (var modifier in classDeclaration.Modifiers)
        {
            switch (modifier.Kind())
            {
                case SyntaxKind.PublicKeyword:
                    return "public";
                case SyntaxKind.InternalKeyword:
                    return "internal";
                case SyntaxKind.PrivateKeyword:
                    return "private";
                case SyntaxKind.ProtectedKeyword:
                    return "protected";
            }
        }
        return "internal";
    }

    private static void GenerateCode(
        SourceProductionContext context,
        ImmutableArray<ContextClassInfo?> contextClasses)
    {
        foreach (var contextClass in contextClasses)
        {
            if (contextClass is not { } ctx)
                continue;

            var diagnostics = new DiagnosticCollector();
            var source = GenerateContextClass(ctx, diagnostics);
            context.AddSource($"{ctx.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));

            foreach (var member in diagnostics.Items)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedType,
                    (member.Location ?? ctx.Location)?.ToLocation() ?? Location.None,
                    member.MemberPath,
                    member.Reason));
            }
        }
    }

    private static string GenerateContextClass(ContextClassInfo contextClass, DiagnosticCollector diagnostics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        sb.AppendLine("using System;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using System.Collections;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using MiniBson;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(contextClass.Namespace))
        {
            sb.AppendLine($"namespace {contextClass.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"{contextClass.Accessibility} partial class {contextClass.ClassName}");
        sb.AppendLine("{");

        // Collect all types that need serialization (including nested types)
        var allTypes = new Dictionary<string, TypeInfo>();
        foreach (var type in contextClass.SerializableTypes)
        {
            CollectAllTypes(type, allTypes);
        }

        // Generate Write/Read/Measure methods for each type
        foreach (var type in allTypes.Values)
        {
            GenerateWriteMethod(sb, type, diagnostics);
            sb.AppendLine();
            GenerateReadMethod(sb, type, diagnostics);
            sb.AppendLine();
            GenerateMeasureMethod(sb, type, diagnostics);
            sb.AppendLine();
        }

        // Generate public Serialize method
        GenerateSerializeMethod(sb, contextClass.SerializableTypes);
        sb.AppendLine();

        // Generate public Deserialize method
        GenerateDeserializeMethod(sb, contextClass.SerializableTypes);
        sb.AppendLine();

        // Generate public GetSerializedSize method
        GenerateGetSerializedSizeMethod(sb, contextClass.SerializableTypes);

        // Runtime backstop for anything MINIBSON001 was reported for
        if (diagnostics.HasAny)
        {
            sb.AppendLine();
            GenerateThrowUnsupportedMethod(sb);
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void CollectAllTypes(TypeInfo type, Dictionary<string, TypeInfo> allTypes)
    {
        if (allTypes.ContainsKey(type.FullyQualifiedName))
            return;

        allTypes[type.FullyQualifiedName] = type;

        // Collect types from properties
        foreach (var property in type.Properties)
        {
            CollectAllTypesFromRef(property.Type, allTypes);
        }
    }

    private static void CollectAllTypesFromRef(TypeRefInfo typeRef, Dictionary<string, TypeInfo> allTypes)
    {
        if (typeRef.NestedTypeInfo is { } nested)
        {
            CollectAllTypes(nested, allTypes);
        }

        if (typeRef.ArrayElementType is { } arrayElement)
        {
            CollectAllTypesFromRef(arrayElement, allTypes);
        }

        if (typeRef.NullableUnderlyingType is { } nullableUnderlying)
        {
            CollectAllTypesFromRef(nullableUnderlying, allTypes);
        }
    }

    private const string ReadOnlyByteMemoryFullName = "global::System.ReadOnlyMemory<byte>";

    /// <summary>Fully qualified so a user type named MiniBson cannot shadow it.</summary>
    private const string SizeType = "global::MiniBson.BsonSize";

    /// <summary>
    /// Supplies a document length only when the writer cannot patch one in later; back-patching
    /// is cheaper than measuring.
    /// </summary>
    private static string SizedFraming(string sizeExpression) =>
        $"writer.RequiresKnownLength ? {sizeExpression} : 0";

    /// <summary>
    /// Type byte plus null-terminated name. Names are known here, so this folds to a literal.
    /// </summary>
    private static int ElementOverhead(string name) => 1 + Encoding.UTF8.GetByteCount(name) + 1;

    /// <summary>
    /// Name of the generated helper that measures one array-typed member. Derived from the
    /// member path so the write and measure emitters agree on it without coordinating.
    /// </summary>
    private static string ArrayMeasureMethodName(EmitScope scope) =>
        "Measure" + scope.MemberPath.Replace(".", "_").Replace("+", "_") + "Array";

    /// <summary>
    /// The BSON representation a model type maps onto.
    /// </summary>
    /// <remarks>
    /// Scalar member names are load-bearing: emitters build call names from them, so
    /// <c>Int32</c> yields <c>WriteInt32</c>, <c>ReadInt32</c>, and <c>BsonSize.Int32</c>.
    /// Adding a scalar type needs a member here, a <see cref="Map"/> case, and matching members
    /// on those three types — no emitter changes. Renaming one breaks generation silently.
    /// The remaining members are handled case by case, since each direction differs.
    /// </remarks>
    private enum BsonMapping
    {
        /// <summary>No mapping exists. Reported as MINIBSON001 with a runtime backstop.</summary>
        Unsupported,
        Boolean,
        Int32,
        Int64,
        Double,
        String,
        DateTime,
        Guid,
        /// <summary><c>byte[]</c>.</summary>
        Binary,
        /// <summary><c>ReadOnlyMemory&lt;byte&gt;</c>.</summary>
        BinaryMemory,
        Array,
        Nested,
    }

    /// <summary>A model type resolved to its BSON representation.</summary>
    private readonly record struct ValueMapping(
        BsonMapping Kind,
        /// <summary>Cast widening the value to its wire type when writing, or empty.</summary>
        string WriteCast = "",
        /// <summary>Whether reading has to narrow the wire value back to the model type.</summary>
        bool CastOnRead = false,
        /// <summary>Element type, for <see cref="BsonMapping.Array"/>.</summary>
        TypeRefInfo? ElementType = null,
        /// <summary>Target type, for <see cref="BsonMapping.Nested"/>.</summary>
        TypeInfo? NestedType = null);

    /// <summary>
    /// Resolves a model type to its BSON representation. Every emitter dispatches on the result,
    /// so this ordering exists in one place. It is load-bearing: <c>byte[]</c> before arrays in
    /// general, and enums before the <see cref="SpecialType"/> switch, since an enum's own
    /// <see cref="SpecialType"/> is <see cref="SpecialType.None"/>.
    /// </summary>
    private static ValueMapping Map(TypeRefInfo type)
    {
        if (type.ArrayElementType is { SpecialType: SpecialType.System_Byte })
            return new ValueMapping(BsonMapping.Binary);

        if (type.FullyQualifiedName == ReadOnlyByteMemoryFullName)
            return new ValueMapping(BsonMapping.BinaryMemory);

        if (type.ArrayElementType is { } elementType)
            return new ValueMapping(BsonMapping.Array, ElementType: elementType);

        if (type.EnumUnderlyingType is { } enumUnderlying)
            return IsWideIntegral(enumUnderlying)
                ? new ValueMapping(BsonMapping.Int64, WriteCast: "(long)", CastOnRead: true)
                : new ValueMapping(BsonMapping.Int32, WriteCast: "(int)", CastOnRead: true);

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                return new ValueMapping(BsonMapping.Boolean);

            case SpecialType.System_Int32:
                return new ValueMapping(BsonMapping.Int32);

            // Narrower than int32 on the wire, so reading has to narrow back.
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                return new ValueMapping(BsonMapping.Int32, CastOnRead: true);

            case SpecialType.System_Int64:
                return new ValueMapping(BsonMapping.Int64);

            case SpecialType.System_UInt32:
            case SpecialType.System_UInt64:
                return new ValueMapping(BsonMapping.Int64, WriteCast: "(long)", CastOnRead: true);

            case SpecialType.System_Double:
                return new ValueMapping(BsonMapping.Double);

            case SpecialType.System_Single:
                return new ValueMapping(BsonMapping.Double, CastOnRead: true);

            case SpecialType.System_String:
                return new ValueMapping(BsonMapping.String);

            case SpecialType.System_DateTime:
                return new ValueMapping(BsonMapping.DateTime);
        }

        if (type.FullyQualifiedName == "global::System.Guid" || type.Name == "Guid")
            return new ValueMapping(BsonMapping.Guid);

        if (type.NestedTypeInfo is { } nestedType)
            return new ValueMapping(BsonMapping.Nested, NestedType: nestedType);

        return new ValueMapping(BsonMapping.Unsupported);
    }

    /// <summary>Integral types BSON has to widen to int64 because int32 cannot hold them.</summary>
    private static bool IsWideIntegral(SpecialType type) =>
        type == SpecialType.System_Int64
        || type == SpecialType.System_UInt64
        || type == SpecialType.System_UInt32;

    private static bool IsPrimitiveType(ITypeSymbol type)
    {
        // Enums are treated as primitives (mapped to their underlying type)
        if (type.TypeKind == TypeKind.Enum)
            return true;

        return type.SpecialType switch
        {
            SpecialType.System_Boolean => true,
            SpecialType.System_Byte => true,
            SpecialType.System_SByte => true,
            SpecialType.System_Int16 => true,
            SpecialType.System_UInt16 => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_UInt32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_UInt64 => true,
            SpecialType.System_Single => true,
            SpecialType.System_Double => true,
            SpecialType.System_String => true,
            SpecialType.System_DateTime => true,
            _ => type.ToDisplayString() == "System.Guid"
                || type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == ReadOnlyByteMemoryFullName
        };
    }

    private static void GenerateWriteMethod(StringBuilder sb, TypeInfo type, DiagnosticCollector diagnostics)
    {
        var typeName = type.FullyQualifiedName;
        var methodName = GetSafeMethodName(type);

        // Document framing only; the body lives in the Inner method so nested writes and
        // top-level writes share one definition.
        sb.AppendLine($"    private void Write{methodName}(BsonWriter writer, {typeName} instance)");
        sb.AppendLine("    {");
        sb.AppendLine($"        writer.WriteStartDocument({SizedFraming($"{SizeType}.DocumentOverhead + Measure{methodName}Inner(instance)")});");
        sb.AppendLine($"        Write{methodName}Inner(writer, instance);");
        sb.AppendLine("        writer.WriteEndDocument();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    private void Write{methodName}Inner(BsonWriter writer, {typeName} instance)");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");

        // Get all properties including inherited ones
        foreach (var property in type.Properties)
        {
            GenerateWriteProperty(sb, property.Name, property.Type, $"instance.{property.Name}",
                ScopeFor(diagnostics, type, property));
        }

        sb.AppendLine("#nullable restore");
        sb.AppendLine("    }");
    }

    private static EmitScope ScopeFor(DiagnosticCollector diagnostics, TypeInfo type, PropertyInfo property) =>
        new(diagnostics, $"{type.Name}.{property.Name}", property.Location);

    private static void GenerateWriteProperty(StringBuilder sb, string name, TypeRefInfo type, string accessor, EmitScope scope)
    {
        var isNullable = type.IsNullable;
        var underlyingType = type.NullableUnderlyingType ?? type;

        if (isNullable && !type.IsValueType)
        {
            sb.AppendLine($"        if ({accessor} is null)");
            sb.AppendLine($"            writer.WriteNull(\"{name}\");");
            sb.AppendLine("        else");
            sb.AppendLine("        {");
            GenerateWriteValue(sb, name, underlyingType, accessor, "            ", scope);
            sb.AppendLine("        }");
        }
        else if (isNullable && type.IsValueType)
        {
            sb.AppendLine($"        if ({accessor}.HasValue)");
            sb.AppendLine("        {");
            GenerateWriteValue(sb, name, underlyingType, $"{accessor}.Value", "            ", scope);
            sb.AppendLine("        }");
            sb.AppendLine("        else");
            sb.AppendLine($"            writer.WriteNull(\"{name}\");");
        }
        else
        {
            GenerateWriteValue(sb, name, underlyingType, accessor, "        ", scope);
        }
    }

    private static void GenerateWriteValue(StringBuilder sb, string name, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var mapping = Map(type);
        var value = mapping.WriteCast + accessor;

        switch (mapping.Kind)
        {
            case BsonMapping.Binary:
                sb.AppendLine($"{indent}writer.WriteBinary(\"{name}\", {accessor});");
                return;

            case BsonMapping.BinaryMemory:
                sb.AppendLine($"{indent}writer.WriteBinary(\"{name}\", {accessor}.Span);");
                return;

            case BsonMapping.Array:
                // The measure emitter defines this helper; both sides derive the name from scope.
                var arrayMeasure = $"{ArrayMeasureMethodName(scope)}({accessor})";
                sb.AppendLine($"{indent}writer.WriteStartArray(\"{name}\", {SizedFraming(arrayMeasure)});");
                sb.AppendLine($"{indent}foreach (var item in {accessor})");
                sb.AppendLine($"{indent}{{");
                GenerateWriteArrayElement(sb, mapping.ElementType!, "item", indent + "    ", scope.Element());
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}writer.WriteEndArray();");
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                var nestedMeasure = $"{SizeType}.DocumentOverhead + Measure{methodName}Inner({accessor})";
                sb.AppendLine($"{indent}writer.WriteStartDocument(\"{name}\", {SizedFraming(nestedMeasure)});");
                sb.AppendLine($"{indent}Write{methodName}Inner(writer, {accessor});");
                sb.AppendLine($"{indent}writer.WriteEndDocument();");
                return;

            case BsonMapping.Unsupported:
                scope.UnsupportedType(sb, indent, type);
                return;

            default:
                sb.AppendLine($"{indent}writer.Write{mapping.Kind}(\"{name}\", {value});");
                return;
        }
    }

    private static void GenerateWriteArrayElement(StringBuilder sb, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var isNullable = type.IsNullable;
        var underlyingType = type.NullableUnderlyingType ?? type;

        if (isNullable && !type.IsValueType)
        {
            sb.AppendLine($"{indent}if ({accessor} is null)");
            sb.AppendLine($"{indent}    writer.WriteNull();");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}{{");
            GenerateWriteArrayElementValue(sb, underlyingType, accessor, indent + "    ", scope);
            sb.AppendLine($"{indent}}}");
        }
        else if (isNullable && type.IsValueType)
        {
            sb.AppendLine($"{indent}if ({accessor}.HasValue)");
            sb.AppendLine($"{indent}{{");
            GenerateWriteArrayElementValue(sb, underlyingType, $"{accessor}.Value", indent + "    ", scope);
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}    writer.WriteNull();");
        }
        else
        {
            GenerateWriteArrayElementValue(sb, underlyingType, accessor, indent, scope);
        }
    }

    private static void GenerateWriteArrayElementValue(StringBuilder sb, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var mapping = Map(type);
        var value = mapping.WriteCast + accessor;

        switch (mapping.Kind)
        {
            // Jagged arrays (including byte[][]) have no element-position mapping
            case BsonMapping.Binary:
            case BsonMapping.Array:
                scope.Unsupported(sb, indent, "nested arrays are not supported", Display(type));
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                var nestedMeasure = $"{SizeType}.DocumentOverhead + Measure{methodName}Inner({accessor})";
                sb.AppendLine($"{indent}writer.WriteStartNestedDocument({SizedFraming(nestedMeasure)});");
                sb.AppendLine($"{indent}Write{methodName}Inner(writer, {accessor});");
                sb.AppendLine($"{indent}writer.WriteEndDocument();");
                return;

            // ReadOnlyMemory<byte> has no name-less write overload.
            case BsonMapping.BinaryMemory:
            case BsonMapping.Unsupported:
                scope.UnsupportedType(sb, indent, type);
                return;

            default:
                sb.AppendLine($"{indent}writer.Write{mapping.Kind}({value});");
                return;
        }
    }

    // Measure emitters. Each one mirrors its Write counterpart above, in the same conditional
    // order, and the two must be changed together: a size that disagrees with what is written
    // makes BsonWriter throw when the document closes.

    private static void GenerateMeasureMethod(StringBuilder sb, TypeInfo type, DiagnosticCollector diagnostics)
    {
        var typeName = type.FullyQualifiedName;
        var methodName = GetSafeMethodName(type);

        // Array members get helper methods, emitted after this one. Both sides call them.
        var helpers = new StringBuilder();

        sb.AppendLine("    /// <summary>Encoded size of this type's elements, excluding framing.</summary>");
        sb.AppendLine($"    private static int Measure{methodName}Inner({typeName} instance)");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("        var __size = 0;");

        foreach (var property in type.Properties)
        {
            GenerateMeasureProperty(sb, helpers, property.Name, property.Type, $"instance.{property.Name}",
                ScopeFor(diagnostics, type, property));
        }

        sb.AppendLine("        return __size;");
        sb.AppendLine("#nullable restore");
        sb.AppendLine("    }");

        sb.Append(helpers);
    }

    private static void GenerateMeasureProperty(StringBuilder sb, StringBuilder helpers, string name, TypeRefInfo type, string accessor, EmitScope scope)
    {
        var isNullable = type.IsNullable;
        var underlyingType = type.NullableUnderlyingType ?? type;

        // WriteNull costs the element header and no value.
        var nullSize = ElementOverhead(name);

        if (isNullable && !type.IsValueType)
        {
            sb.AppendLine($"        if ({accessor} is null)");
            sb.AppendLine($"            __size += {nullSize};");
            sb.AppendLine("        else");
            sb.AppendLine("        {");
            GenerateMeasureValue(sb, helpers, name, underlyingType, accessor, "            ", scope);
            sb.AppendLine("        }");
        }
        else if (isNullable && type.IsValueType)
        {
            sb.AppendLine($"        if ({accessor}.HasValue)");
            sb.AppendLine("        {");
            GenerateMeasureValue(sb, helpers, name, underlyingType, $"{accessor}.Value", "            ", scope);
            sb.AppendLine("        }");
            sb.AppendLine("        else");
            sb.AppendLine($"            __size += {nullSize};");
        }
        else
        {
            GenerateMeasureValue(sb, helpers, name, underlyingType, accessor, "        ", scope);
        }
    }

    private static void GenerateMeasureValue(StringBuilder sb, StringBuilder helpers, string name, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var mapping = Map(type);

        // Both this and the BsonSize consts fold, so fixed-size values cost nothing at runtime.
        var overhead = ElementOverhead(name);
        void Add(string valueSize) => sb.AppendLine($"{indent}__size += {overhead} + {valueSize};");

        switch (mapping.Kind)
        {
            case BsonMapping.Binary:
            case BsonMapping.BinaryMemory:
                Add($"{SizeType}.Binary({accessor}.Length)");
                return;

            case BsonMapping.Array:
                var helperName = ArrayMeasureMethodName(scope);
                GenerateArrayMeasureHelper(helpers, helperName, type, mapping.ElementType!, scope.Element());
                Add($"{helperName}({accessor})");
                return;

            case BsonMapping.String:
                Add($"{SizeType}.String({accessor})");
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                Add($"{SizeType}.DocumentOverhead + Measure{methodName}Inner({accessor})");
                return;

            case BsonMapping.Unsupported:
                scope.UnsupportedType(sb, indent, type);
                return;

            default:
                Add($"{SizeType}.{mapping.Kind}");
                return;
        }
    }

    /// <summary>
    /// Emits the helper measuring one array member. Returns the array's complete document
    /// length, which is what <c>WriteStartArray</c> expects.
    /// </summary>
    private static void GenerateArrayMeasureHelper(StringBuilder helpers, string methodName, TypeRefInfo arrayType, TypeRefInfo elementType, EmitScope elementScope)
    {
        helpers.AppendLine();
        helpers.AppendLine("    /// <summary>Encoded length of this array, framing and keys included.</summary>");
        helpers.AppendLine($"    private static int {methodName}({arrayType.FullyQualifiedName} value)");
        helpers.AppendLine("    {");
        helpers.AppendLine("#nullable disable");
        // Type bytes and index keys are counted in bulk, so the emitters below add only values.
        helpers.AppendLine($"        var __size = {SizeType}.ArrayOverhead(value.Length);");
        helpers.AppendLine("        foreach (var item in value)");
        helpers.AppendLine("        {");
        GenerateMeasureArrayElement(helpers, elementType, "item", "            ", elementScope);
        helpers.AppendLine("        }");
        helpers.AppendLine("        return __size;");
        helpers.AppendLine("#nullable restore");
        helpers.AppendLine("    }");
    }

    private static void GenerateMeasureArrayElement(StringBuilder sb, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var isNullable = type.IsNullable;
        var underlyingType = type.NullableUnderlyingType ?? type;

        // A null element's header is already counted by ArrayOverhead, so there is no else.
        if (isNullable && !type.IsValueType)
        {
            sb.AppendLine($"{indent}if ({accessor} is not null)");
            sb.AppendLine($"{indent}{{");
            GenerateMeasureArrayElementValue(sb, underlyingType, accessor, indent + "    ", scope);
            sb.AppendLine($"{indent}}}");
        }
        else if (isNullable && type.IsValueType)
        {
            sb.AppendLine($"{indent}if ({accessor}.HasValue)");
            sb.AppendLine($"{indent}{{");
            GenerateMeasureArrayElementValue(sb, underlyingType, $"{accessor}.Value", indent + "    ", scope);
            sb.AppendLine($"{indent}}}");
        }
        else
        {
            GenerateMeasureArrayElementValue(sb, underlyingType, accessor, indent, scope);
        }
    }

    private static void GenerateMeasureArrayElementValue(StringBuilder sb, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var mapping = Map(type);

        void Add(string valueSize) => sb.AppendLine($"{indent}__size += {valueSize};");

        switch (mapping.Kind)
        {
            // Jagged arrays (including byte[][]) have no element-position mapping
            case BsonMapping.Binary:
            case BsonMapping.Array:
                scope.Unsupported(sb, indent, "nested arrays are not supported", Display(type));
                return;

            case BsonMapping.String:
                Add($"{SizeType}.String({accessor})");
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                Add($"{SizeType}.DocumentOverhead + Measure{methodName}Inner({accessor})");
                return;

            // ReadOnlyMemory<byte> has no name-less write overload.
            case BsonMapping.BinaryMemory:
            case BsonMapping.Unsupported:
                scope.UnsupportedType(sb, indent, type);
                return;

            default:
                Add($"{SizeType}.{mapping.Kind}");
                return;
        }
    }

    private static void GenerateReadMethod(StringBuilder sb, TypeInfo type, DiagnosticCollector diagnostics)
    {
        var typeName = type.FullyQualifiedName;
        var methodName = GetSafeMethodName(type);

        sb.AppendLine($"    private {typeName}? Read{methodName}(BsonReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("        reader.ReadStartDocument();");
        sb.AppendLine($"        var result = Read{methodName}Inner(reader);");
        sb.AppendLine("        reader.ReadEndDocument();");
        sb.AppendLine("        return result;");
        sb.AppendLine("#nullable restore");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Generate inner read method (without ReadStartDocument/ReadEndDocument)
        sb.AppendLine($"    private {typeName} Read{methodName}Inner(BsonReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");

        // Declare variables for all properties including inherited
        foreach (var property in type.Properties)
        {
            var propertyType = property.Type.FullyQualifiedName;
            var defaultValue = GetDefaultValue(property.Type);
            sb.AppendLine($"        {propertyType} _{property.Name} = {defaultValue};");
        }

        sb.AppendLine();
        sb.AppendLine("        while (reader.Read())");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (reader.CurrentName)");
        sb.AppendLine("            {");

        foreach (var property in type.Properties)
        {
            sb.AppendLine($"                case \"{property.Name}\":");
            GenerateReadProperty(sb, property.Name, property.Type, "                    ",
                ScopeFor(diagnostics, type, property));
            sb.AppendLine("                    break;");
        }

        sb.AppendLine("                default:");
        sb.AppendLine("                    reader.Skip();");
        sb.AppendLine("                    break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        // Create and return the object
        var properties = type.Properties.ToList();
        
        // Check if type is a record
        var isRecord = type.IsRecord;

        if (isRecord)
        {
            // Use constructor for records
            sb.AppendLine($"        return new {typeName}(");
            var first = true;
            foreach (var property in properties)
            {
                if (!first)
                    sb.AppendLine(",");
                else
                    first = false;

                sb.Append($"            _{property.Name}");
            }
            sb.AppendLine();
            sb.AppendLine("        );");
        }
        else if (properties.FirstOrDefault(p => !p.IsSettable) is { } readOnlyProperty)
        {
            // An object initializer can't assign this, and emitting one anyway buries the
            // user in CS0200s pointing at generated code. Report it as MINIBSON001 instead.
            ScopeFor(diagnostics, type, readOnlyProperty).Unsupported(
                sb,
                "        ",
                $"property '{readOnlyProperty.Name}' has no public setter, so the type cannot be deserialized",
                Display(type.FullyQualifiedName));
            sb.AppendLine("        return default;");
        }
        else
        {
            // Use object initializer for classes
            sb.AppendLine($"        return new {typeName}");
            sb.AppendLine("        {");

            var first = true;
            foreach (var property in properties)
            {
                if (!first)
                    sb.AppendLine(",");
                else
                    first = false;

                sb.Append($"            {property.Name} = _{property.Name}");
            }

            sb.AppendLine();
            sb.AppendLine("        };");
        }
        sb.AppendLine("#nullable restore");
        sb.AppendLine("    }");
    }

    private static string GetDefaultValue(TypeRefInfo type)
    {
        if (type.IsNullable || !type.IsValueType)
            return "default!";

        return type.SpecialType switch
        {
            SpecialType.System_Boolean => "false",
            SpecialType.System_Int32 => "0",
            SpecialType.System_Int64 => "0L",
            SpecialType.System_Double => "0.0",
            SpecialType.System_Single => "0.0f",
            SpecialType.System_String => "string.Empty",
            _ => "default!"
        };
    }

    private static void GenerateReadProperty(StringBuilder sb, string name, TypeRefInfo type, string indent, EmitScope scope)
    {
        var underlyingType = type.NullableUnderlyingType ?? type;
        var mapping = Map(underlyingType);

        sb.AppendLine($"{indent}if (reader.CurrentType == BsonType.Null)");
        sb.AppendLine($"{indent}    _{name} = default;");
        sb.AppendLine($"{indent}else");

        switch (mapping.Kind)
        {
            case BsonMapping.Binary:
                sb.AppendLine($"{indent}    _{name} = reader.ReadBinary().Data;");
                return;

            case BsonMapping.BinaryMemory:
                sb.AppendLine($"{indent}    _{name} = reader.ReadBinaryAsMemory().Data;");
                return;

            case BsonMapping.Array:
                GenerateReadArray(sb, name, mapping.ElementType!, indent + "    ", scope.Element());
                return;

            default:
                GenerateReadValue(sb, name, underlyingType, indent + "    ", scope);
                return;
        }
    }

    private static void GenerateReadArray(StringBuilder sb, string name, TypeRefInfo elementType, string indent, EmitScope scope)
    {
        var elementTypeName = elementType.FullyQualifiedName;

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var list = new global::System.Collections.Generic.List<{elementTypeName}>();");
        sb.AppendLine($"{indent}    reader.ReadStartArray();");
        sb.AppendLine($"{indent}    while (reader.Read())");
        sb.AppendLine($"{indent}    {{");

        var isElementNullable = elementType.IsNullable;
        var underlyingElementType = elementType.NullableUnderlyingType ?? elementType;

        if (isElementNullable || !elementType.IsValueType)
        {
            sb.AppendLine($"{indent}        if (reader.CurrentType == BsonType.Null)");
            sb.AppendLine($"{indent}            list.Add(default!);");
            sb.AppendLine($"{indent}        else");
            GenerateReadArrayElement(sb, underlyingElementType, indent + "            ", scope);
        }
        else
        {
            GenerateReadArrayElement(sb, underlyingElementType, indent + "        ", scope);
        }

        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    reader.ReadEndDocument();");
        sb.AppendLine($"{indent}    _{name} = list.ToArray();");
        sb.AppendLine($"{indent}}}");
    }

    private static void GenerateReadArrayElement(StringBuilder sb, TypeRefInfo type, string indent, EmitScope scope)
    {
        var mapping = Map(type);
        var cast = mapping.CastOnRead ? $"({type.FullyQualifiedName})" : "";

        switch (mapping.Kind)
        {
            // Jagged arrays (including byte[][]) have no element-position mapping
            case BsonMapping.Binary:
            case BsonMapping.Array:
                scope.Unsupported(sb, indent, "nested arrays are not supported", Display(type));
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    reader.ReadStartNestedDocument();");
                sb.AppendLine($"{indent}    list.Add(Read{methodName}Inner(reader));");
                sb.AppendLine($"{indent}    reader.ReadEndDocument();");
                sb.AppendLine($"{indent}}}");
                return;

            case BsonMapping.BinaryMemory:
            case BsonMapping.Unsupported:
                scope.UnsupportedType(sb, indent, type);
                return;

            default:
                sb.AppendLine($"{indent}list.Add({cast}reader.Read{mapping.Kind}());");
                return;
        }
    }

    private static void GenerateReadValue(StringBuilder sb, string name, TypeRefInfo type, string indent, EmitScope scope)
    {
        var mapping = Map(type);
        var cast = mapping.CastOnRead ? $"({type.FullyQualifiedName})" : "";

        switch (mapping.Kind)
        {
            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    reader.ReadStartNestedDocument();");
                sb.AppendLine($"{indent}    _{name} = Read{methodName}Inner(reader);");
                sb.AppendLine($"{indent}    reader.ReadEndDocument();");
                sb.AppendLine($"{indent}}}");
                return;

            // GenerateReadProperty resolves these before delegating here.
            case BsonMapping.Binary:
            case BsonMapping.BinaryMemory:
            case BsonMapping.Array:
            case BsonMapping.Unsupported:
                scope.UnsupportedType(sb, indent, type);
                return;

            default:
                sb.AppendLine($"{indent}_{name} = {cast}reader.Read{mapping.Kind}();");
                return;
        }
    }

    private static void GenerateThrowUnsupportedMethod(StringBuilder sb)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Backstop for members reported as MINIBSON001. Reached only if that");
        sb.AppendLine("    /// diagnostic was downgraded or suppressed.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static void ThrowUnsupported(string member, string type)");
        sb.AppendLine("        => throw new NotSupportedException(");
        sb.AppendLine("            $\"MiniBson cannot serialize '{member}': type '{type}' is not supported \" +");
        sb.AppendLine("            \"by the source generator (MINIBSON001).\");");
    }

    private static void GenerateGetSerializedSizeMethod(StringBuilder sb, EquatableList<TypeInfo> types)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Returns the exact number of bytes <see cref=\"Serialize\"/> would write for this value.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <remarks>");
        sb.AppendLine("    /// Exact rather than approximate, provided every property returns the same value when read");
        sb.AppendLine("    /// twice. This is the same size the writer computes for itself when the destination cannot");
        sb.AppendLine("    /// be seeked.");
        sb.AppendLine("    /// </remarks>");
        sb.AppendLine("    public int GetSerializedSize(object input)");
        sb.AppendLine("    {");
        sb.AppendLine("        var inputType = input.GetType();");

        var firstSize = true;
        foreach (var type in types)
        {
            var typeName = type.FullyQualifiedName;
            var methodName = GetSafeMethodName(type);

            sb.AppendLine(firstSize
                ? $"        if (inputType == typeof({typeName}))"
                : $"        else if (inputType == typeof({typeName}))");
            firstSize = false;

            sb.AppendLine($"            return {SizeType}.DocumentOverhead + Measure{methodName}Inner(({typeName})input);");
        }

        sb.AppendLine("        else");
        sb.AppendLine("            throw new NotSupportedException($\"Type {input.GetType()} is not supported for serialization.\");");
        sb.AppendLine("    }");
    }

    private static void GenerateSerializeMethod(StringBuilder sb, EquatableList<TypeInfo> types)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Serializes the specified object to BSON format.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public void Serialize(object input, BsonWriter writer)");
        sb.AppendLine("    {");
        sb.AppendLine("        var inputType = input.GetType();");

        var first = true;
        foreach (var type in types)
        {
            var typeName = type.FullyQualifiedName;
            var methodName = GetSafeMethodName(type);

            if (first)
            {
                sb.AppendLine($"        if (inputType == typeof({typeName}))");
                first = false;
            }
            else
            {
                sb.AppendLine($"        else if (inputType == typeof({typeName}))");
            }

            sb.AppendLine($"            Write{methodName}(writer, ({typeName})input);");
        }

        sb.AppendLine("        else");
        sb.AppendLine("            throw new NotSupportedException($\"Type {input.GetType()} is not supported for serialization.\");");
        sb.AppendLine("    }");
    }

    private static void GenerateDeserializeMethod(StringBuilder sb, EquatableList<TypeInfo> types)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Deserializes BSON data to an object of the specified type.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public object? Deserialize(BsonReader reader, Type type)");
        sb.AppendLine("    {");

        var first = true;
        foreach (var type in types)
        {
            var typeName = type.FullyQualifiedName;
            var methodName = GetSafeMethodName(type);

            if (first)
            {
                sb.AppendLine($"        if (type == typeof({typeName}))");
                first = false;
            }
            else
            {
                sb.AppendLine($"        else if (type == typeof({typeName}))");
            }

            sb.AppendLine($"            return Read{methodName}(reader);");
        }

        sb.AppendLine();
        sb.AppendLine("        throw new NotSupportedException($\"Type {type} is not supported for deserialization.\");");
        sb.AppendLine("    }");
    }

    private static IEnumerable<IPropertySymbol> GetAllProperties(INamedTypeSymbol type)
    {
        // Collect types from derived to base
        var typeHierarchy = new List<INamedTypeSymbol>();
        var currentType = type;
        
        while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
        {
            typeHierarchy.Add(currentType);
            currentType = currentType.BaseType;
        }
        
        // Process in derived-to-base order (no reversal)
        // This means properties from the most derived class come first
        var properties = new List<IPropertySymbol>();
        var seenPropertyNames = new HashSet<string>();
        
        foreach (var typeInHierarchy in typeHierarchy)
        {
            foreach (var member in typeInHierarchy.GetMembers())
            {
                if (member is IPropertySymbol property &&
                    property.DeclaredAccessibility == Accessibility.Public &&
                    !property.IsStatic &&
                    property.GetMethod != null)
                {
                    // Only add if not already added (handles property hiding/new)
                    if (seenPropertyNames.Add(property.Name))
                    {
                        properties.Add(property);
                    }
                }
            }
        }
        
        return properties;
    }

    private static string GetSafeMethodName(TypeInfo type)
    {
        // Create a safe method name from the type name
        return type.Name.Replace(".", "_").Replace("+", "_");
    }

    private static TypeInfo ExtractTypeInfo(INamedTypeSymbol symbol, HashSet<INamedTypeSymbol>? visited = null)
    {
        visited ??= new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        if (!visited.Add(symbol))
            return new TypeInfo(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                symbol.Name,
                false,
                symbol.IsValueType,
                EquatableList<PropertyInfo>.Empty);

        var properties = GetAllProperties(symbol)
            .Select(p => new PropertyInfo(
                p.Name,
                ExtractTypeRefInfo(p.Type, visited),
                LocationInfo.From(p),
                p.SetMethod is { DeclaredAccessibility: Accessibility.Public }))
            .ToList();

        return new TypeInfo(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            symbol.Name,
            symbol.IsRecord && symbol.TypeKind == TypeKind.Class,
            symbol.IsValueType,
            new EquatableList<PropertyInfo>(properties));
    }

    private static TypeRefInfo ExtractTypeRefInfo(ITypeSymbol symbol, HashSet<INamedTypeSymbol> visited)
    {
        var isNullableValueType = symbol is INamedTypeSymbol { IsGenericType: true } nt &&
                                  nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        var isNullable = isNullableValueType || symbol.NullableAnnotation == NullableAnnotation.Annotated;

        TypeRefInfo? nullableUnderlying = null;
        if (isNullableValueType && symbol is INamedTypeSymbol nvt)
        {
            nullableUnderlying = ExtractTypeRefInfo(nvt.TypeArguments[0], visited);
        }

        TypeRefInfo? arrayElement = null;
        if (symbol is IArrayTypeSymbol arrayType)
        {
            arrayElement = ExtractTypeRefInfo(arrayType.ElementType, visited);
        }

        SpecialType? enumUnderlying = null;
        if (symbol.TypeKind == TypeKind.Enum && symbol is INamedTypeSymbol enumType)
        {
            enumUnderlying = enumType.EnumUnderlyingType?.SpecialType;
        }

        TypeInfo? nestedTypeInfo = null;
        if (symbol is INamedTypeSymbol namedType &&
            !IsPrimitiveType(symbol) &&
            // decimal has no BSON mapping here; treating it as a POCO would silently
            // emit an empty document, so let it fall through to MINIBSON001 instead.
            symbol.SpecialType != SpecialType.System_Decimal &&
            symbol is not IArrayTypeSymbol &&
            !isNullableValueType)
        {
            nestedTypeInfo = ExtractTypeInfo(namedType, visited);
        }

        return new TypeRefInfo(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            symbol is INamedTypeSymbol ns ? ns.Name : symbol.Name,
            symbol.SpecialType,
            symbol.IsValueType,
            isNullable,
            symbol.NullableAnnotation,
            symbol.TypeKind,
            enumUnderlying,
            arrayElement,
            nullableUnderlying,
            nestedTypeInfo);
    }
}
