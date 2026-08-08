using System;
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
    /// A member that the emitters cannot write code for.
    /// </summary>
    private sealed record UnsupportedMember(string MemberPath, string Reason, LocationInfo? Location);

    /// <summary>
    /// Collects the unsupported members of one context class. The write emitters and the read
    /// emitters both read each member. Thus this type keeps one entry for each path and reason.
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
    /// Names the member that the generator writes code for now. Thus the generator can report
    /// an unsupported type against that member instead of a member with no code.
    /// </summary>
    /// <param name="MemberPath">The name of the member in a diagnostic to the user.</param>
    /// <param name="MethodPath">
    /// The name of the member in a generated identifier. It is different from
    /// <paramref name="MemberPath"/>, because that path uses the simple type names. Two models
    /// in different namespaces can have the same simple name.
    /// </param>
    private sealed record EmitScope(
        DiagnosticCollector Diagnostics,
        string MemberPath,
        string MethodPath,
        LocationInfo? Location)
    {
        public EmitScope Element() => this with
        {
            MemberPath = MemberPath + "[]",
            MethodPath = MethodPath + "_Element",
        };

        /// <summary>
        /// Records a diagnostic and writes a fallback that throws an exception. Thus the
        /// generated code stays correct and causes no more compiler errors. If the user
        /// suppresses the diagnostic, the code throws an exception and loses no data.
        /// </summary>
        public void Unsupported(StringBuilder sb, string indent, string reason, string typeName)
        {
            Diagnostics.Report(new UnsupportedMember(MemberPath, reason, Location));
            sb.AppendLine($"{indent}ThrowUnsupported(\"{MemberPath}\", \"{typeName}\");");
        }

        /// <summary>Reports a type that has no read mapping and no write mapping.</summary>
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

        // There is no class for the generated code. The user sees error CS1061 on the call to
        // Serialize. The user does not see a diagnostic that points here.
        if (!classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            return null;

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

    /// <summary>
    /// The accessibility of the generated context partial. Only <c>public</c> and
    /// <c>internal</c> are legal at namespace scope, and this generator writes its code there.
    /// Thus each other accessibility becomes <c>internal</c>. A context in another type is not
    /// supported, because the generated half is still at namespace scope.
    /// </summary>
    private static string GetAccessibility(ClassDeclarationSyntax classDeclaration) =>
        classDeclaration.Modifiers.Any(SyntaxKind.PublicKeyword) ? "public" : "internal";

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

        // A model that the generator finds through a property also needs methods. Thus the
        // registered types are only the first types.
        var allTypes = new Dictionary<string, TypeInfo>();
        foreach (var type in contextClass.SerializableTypes)
        {
            CollectAllTypes(type, allTypes);
        }

        foreach (var type in allTypes.Values)
        {
            GenerateWriteMethod(sb, type, diagnostics);
            sb.AppendLine();
            GenerateReadMethod(sb, type, diagnostics);
            sb.AppendLine();
            GenerateMeasureMethod(sb, type, diagnostics);
            sb.AppendLine();
        }

        // Only a registered type is a valid top-level value. Thus the public methods dispatch on
        // those types and not on each type above.
        GenerateSerializeMethod(sb, contextClass.SerializableTypes);
        sb.AppendLine();

        GenerateDeserializeMethod(sb, contextClass.SerializableTypes);
        sb.AppendLine();

        GenerateGetSerializedSizeMethod(sb, contextClass.SerializableTypes);

        // The fallback for each member with a MINIBSON001 diagnostic.
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

    /// <summary>A fully qualified name, so a user type with the name MiniBson cannot hide it.</summary>
    private const string SizeType = "global::MiniBson.BsonSize";

    /// <summary>A fully qualified name, so a user type with the name MiniBson cannot hide it.</summary>
    private const string SizeTableType = "global::MiniBson.BsonSizeTable";

    /// <summary>
    /// The size table that both passes use. This name prevents a collision, and it is not a
    /// name for a user to read. It shares its scope with each member of the model.
    /// </summary>
    private const string SizeTableParameter = "__sizes";

    /// <summary>
    /// The length for the start of a document. The measure pass recorded the lengths in the order
    /// that the write pass asks for them here.
    /// </summary>
    private const string SizedFraming = SizeTableParameter + ".Next()";

    /// <summary>
    /// The type byte and the name with its null terminator. The generator knows the names here,
    /// so this value becomes a literal.
    /// </summary>
    private static int ElementOverhead(string name) => 1 + Encoding.UTF8.GetByteCount(name) + 1;

    /// <summary>
    /// The name of the generated helper that measures one array member. All such helpers for all
    /// models go into one partial class. Thus this name comes from
    /// <see cref="EmitScope.MethodPath"/> and not from <see cref="EmitScope.MemberPath"/>.
    /// <see cref="EmitScope.MemberPath"/> keeps the simple type names, and two models with the
    /// same simple name would give this helper two times.
    /// </summary>
    private static string ArrayMeasureMethodName(EmitScope scope) =>
        "Measure" + scope.MethodPath + "Array";

    /// <summary>
    /// The BSON representation of a model type.
    /// </summary>
    /// <remarks>
    /// You must not change the names of the scalar members. The emitters build method names
    /// from them, so <c>Int32</c> gives <c>WriteInt32</c>, <c>ReadInt32</c>, and
    /// <c>BsonSize.Int32</c>. To add a scalar type, add a member here, a <see cref="Map"/> case,
    /// and the equivalent members on those three types. No emitter needs a change. A new name
    /// for a member breaks the code generation and gives no error. The other members have
    /// separate code in each emitter, because the read direction and the write direction are
    /// different for them.
    /// </remarks>
    private enum BsonMapping
    {
        /// <summary>There is no mapping. The generator reports MINIBSON001 and writes a fallback.</summary>
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

    /// <summary>A model type with its BSON representation.</summary>
    private readonly record struct ValueMapping(
        BsonMapping Kind,
        /// <summary>The cast that makes the value wider for the wire, or an empty string.</summary>
        string WriteCast = "",
        /// <summary>True when a read must make the wire value narrow again for the model type.</summary>
        bool CastOnRead = false,
        /// <summary>The element type, for <see cref="BsonMapping.Array"/>.</summary>
        TypeRefInfo? ElementType = null,
        /// <summary>The target type, for <see cref="BsonMapping.Nested"/>.</summary>
        TypeInfo? NestedType = null);

    /// <summary>
    /// Gives the BSON representation of a model type. Each emitter dispatches on the result, so
    /// this order is in one place. You must not change the order: <c>byte[]</c> comes before the
    /// general array test, and the enums come before the <see cref="SpecialType"/> switch. The
    /// <see cref="SpecialType"/> of an enum is <see cref="SpecialType.None"/>.
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

            // This type is narrower than an int32 on the wire, so a read must make it narrow again.
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

        // The test uses only the fully qualified name. A test on the simple name would also
        // accept a user type with the name Guid. Such a type comes here before the Nested case
        // below, and the generator would write a WriteGuid call that does not compile.
        if (type.FullyQualifiedName == "global::System.Guid")
            return new ValueMapping(BsonMapping.Guid);

        if (type.NestedTypeInfo is { } nestedType)
            return new ValueMapping(BsonMapping.Nested, NestedType: nestedType);

        return new ValueMapping(BsonMapping.Unsupported);
    }

    /// <summary>The integer types that BSON must make int64, because an int32 is too small.</summary>
    private static bool IsWideIntegral(SpecialType type) =>
        type == SpecialType.System_Int64
        || type == SpecialType.System_UInt64
        || type == SpecialType.System_UInt32;

    private static bool IsPrimitiveType(ITypeSymbol type)
    {
        // An enum is a primitive here. It maps to its underlying type.
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

        // This method writes only the document start and end. The body is in the Inner method,
        // so a nested write and a top-level write use one definition.
        sb.AppendLine($"    private void Write{methodName}(BsonWriter writer, {typeName} instance)");
        sb.AppendLine("    {");
        sb.AppendLine("        // One measuring walk for the whole graph, replayed below in the order the writer");
        sb.AppendLine("        // asks for lengths. Every document needs its length before it starts.");
        sb.AppendLine($"        var {SizeTableParameter} = {SizeTableType}.Rent();");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine($"            Measure{methodName}(instance, {SizeTableParameter});");
        sb.AppendLine();
        sb.AppendLine($"            writer.WriteStartDocument({SizedFraming});");
        sb.AppendLine($"            Write{methodName}Inner(writer, instance, {SizeTableParameter});");
        sb.AppendLine("            writer.WriteEndDocument();");
        sb.AppendLine("        }");
        sb.AppendLine("        finally");
        sb.AppendLine("        {");
        sb.AppendLine($"            {SizeTableParameter}.Return();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    private void Write{methodName}Inner(BsonWriter writer, {typeName} instance, {SizeTableType} {SizeTableParameter})");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");

        foreach (var property in type.Properties)
        {
            GenerateWriteProperty(sb, property.Name, property.Type, $"instance.{property.Name}",
                ScopeFor(diagnostics, type, property));
        }

        sb.AppendLine("#nullable restore");
        sb.AppendLine("    }");
    }

    private static EmitScope ScopeFor(DiagnosticCollector diagnostics, TypeInfo type, PropertyInfo property) =>
        new(
            diagnostics,
            $"{type.Name}.{property.Name}",
            $"{GetSafeMethodName(type)}_{property.Name}",
            property.Location);

    private static void GenerateWriteProperty(StringBuilder sb, string name, TypeRefInfo type, string accessor, EmitScope scope)
    {
        var isNullable = type.IsNullable;
        var underlyingType = type.NullableUnderlyingType ?? type;

        // The generated code tests each reference, with an annotation and without one. A model
        // that the compiler built without nullable reference types can still hold a null. BSON
        // has no encoding of a null as a string or as binary data. Thus a measurement would give
        // a document that the writer cannot then write.
        if (!type.IsValueType)
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
                sb.AppendLine($"{indent}writer.WriteStartArray(\"{name}\", {SizedFraming});");
                sb.AppendLine($"{indent}foreach (var item in {accessor})");
                sb.AppendLine($"{indent}{{");
                GenerateWriteArrayElement(sb, mapping.ElementType!, "item", indent + "    ", scope.Element());
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}writer.WriteEndArray();");
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                sb.AppendLine($"{indent}writer.WriteStartDocument(\"{name}\", {SizedFraming});");
                sb.AppendLine($"{indent}Write{methodName}Inner(writer, {accessor}, {SizeTableParameter});");
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

        // With an annotation and without one. See GenerateWriteProperty.
        if (!type.IsValueType)
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
            // A jagged array, and this includes byte[][], has no mapping for an element position.
            case BsonMapping.Binary:
            case BsonMapping.Array:
                scope.Unsupported(sb, indent, "nested arrays are not supported", Display(type));
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                sb.AppendLine($"{indent}writer.WriteStartNestedDocument({SizedFraming});");
                sb.AppendLine($"{indent}Write{methodName}Inner(writer, {accessor}, {SizeTableParameter});");
                sb.AppendLine($"{indent}writer.WriteEndDocument();");
                return;

            // ReadOnlyMemory<byte> has no write overload without a name.
            case BsonMapping.BinaryMemory:
            case BsonMapping.Unsupported:
                scope.UnsupportedType(sb, indent, type);
                return;

            default:
                sb.AppendLine($"{indent}writer.Write{mapping.Kind}({value});");
                return;
        }
    }

    // The measure emitters. Each one agrees with its Write equivalent above and uses the same
    // order of tests. You must change the two together. A length that does not agree with the
    // bytes makes BsonWriter throw an exception at the end of the document.

    private static void GenerateMeasureMethod(StringBuilder sb, TypeInfo type, DiagnosticCollector diagnostics)
    {
        var typeName = type.FullyQualifiedName;
        var methodName = GetSafeMethodName(type);

        // An array member gets a helper method after this one. Both directions call it.
        var helpers = new StringBuilder();

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// The encoded length of this type as a document. This method records that length and");
        sb.AppendLine("    /// the length of each nested document, in the order that the write pass asks for them.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    private static int Measure{methodName}({typeName} instance, {SizeTableType} {SizeTableParameter})");
        sb.AppendLine("    {");
        sb.AppendLine($"        var __slot = {SizeTableParameter}.Reserve();");
        sb.AppendLine($"        var __size = {SizeType}.DocumentOverhead + Measure{methodName}Inner(instance, {SizeTableParameter});");
        sb.AppendLine($"        {SizeTableParameter}.Record(__slot, __size);");
        sb.AppendLine("        return __size;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    /// <summary>The encoded length of the elements of this type, without the document overhead.</summary>");
        sb.AppendLine($"    private static int Measure{methodName}Inner({typeName} instance, {SizeTableType} {SizeTableParameter})");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");
        // A checked block. A length that wrapped would agree with no other length, and the
        // writer would start a document that it cannot describe.
        sb.AppendLine("        checked");
        sb.AppendLine("        {");
        sb.AppendLine("            var __size = 0;");

        foreach (var property in type.Properties)
        {
            GenerateMeasureProperty(sb, helpers, property.Name, property.Type, $"instance.{property.Name}",
                "            ", ScopeFor(diagnostics, type, property));
        }

        sb.AppendLine("            return __size;");
        sb.AppendLine("        }");
        sb.AppendLine("#nullable restore");
        sb.AppendLine("    }");

        sb.Append(helpers);
    }

    private static void GenerateMeasureProperty(StringBuilder sb, StringBuilder helpers, string name, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var isNullable = type.IsNullable;
        var underlyingType = type.NullableUnderlyingType ?? type;

        // WriteNull writes the element header and no value.
        var nullSize = ElementOverhead(name);

        // With an annotation and without one. See GenerateWriteProperty.
        if (!type.IsValueType)
        {
            sb.AppendLine($"{indent}if ({accessor} is null)");
            sb.AppendLine($"{indent}    __size += {nullSize};");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}{{");
            GenerateMeasureValue(sb, helpers, name, underlyingType, accessor, indent + "    ", scope);
            sb.AppendLine($"{indent}}}");
        }
        else if (isNullable && type.IsValueType)
        {
            sb.AppendLine($"{indent}if ({accessor}.HasValue)");
            sb.AppendLine($"{indent}{{");
            GenerateMeasureValue(sb, helpers, name, underlyingType, $"{accessor}.Value", indent + "    ", scope);
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}    __size += {nullSize};");
        }
        else
        {
            GenerateMeasureValue(sb, helpers, name, underlyingType, accessor, indent, scope);
        }
    }

    private static void GenerateMeasureValue(StringBuilder sb, StringBuilder helpers, string name, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var mapping = Map(type);

        // The compiler folds this value and the BsonSize const members. Thus a value with a
        // fixed length costs nothing at run time.
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
                Add($"{helperName}({accessor}, {SizeTableParameter})");
                return;

            case BsonMapping.String:
                Add($"{SizeType}.String({accessor})");
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                Add($"Measure{methodName}({accessor}, {SizeTableParameter})");
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
    /// Writes the helper that measures one array member. The helper returns the full document
    /// length of the array, which is the value that <c>WriteStartArray</c> needs.
    /// </summary>
    private static void GenerateArrayMeasureHelper(StringBuilder helpers, string methodName, TypeRefInfo arrayType, TypeRefInfo elementType, EmitScope elementScope)
    {
        helpers.AppendLine();
        helpers.AppendLine("    /// <summary>The encoded length of this array, with its overhead and its keys.</summary>");
        helpers.AppendLine($"    private static int {methodName}({arrayType.FullyQualifiedName} value, {SizeTableType} {SizeTableParameter})");
        helpers.AppendLine("    {");
        helpers.AppendLine("#nullable disable");
        helpers.AppendLine("        checked");
        helpers.AppendLine("        {");
        helpers.AppendLine($"            var __slot = {SizeTableParameter}.Reserve();");
        // This code counts the type bytes and the index keys together. Thus the emitters below
        // add only the values.
        helpers.AppendLine($"            var __size = {SizeType}.ArrayOverhead(value.Length);");
        helpers.AppendLine("            foreach (var item in value)");
        helpers.AppendLine("            {");
        GenerateMeasureArrayElement(helpers, elementType, "item", "                ", elementScope);
        helpers.AppendLine("            }");
        helpers.AppendLine($"            {SizeTableParameter}.Record(__slot, __size);");
        helpers.AppendLine("            return __size;");
        helpers.AppendLine("        }");
        helpers.AppendLine("#nullable restore");
        helpers.AppendLine("    }");
    }

    private static void GenerateMeasureArrayElement(StringBuilder sb, TypeRefInfo type, string accessor, string indent, EmitScope scope)
    {
        var isNullable = type.IsNullable;
        var underlyingType = type.NullableUnderlyingType ?? type;

        // ArrayOverhead already counts the header of a null element, so there is no else block.
        // With an annotation and without one. See GenerateWriteProperty.
        if (!type.IsValueType)
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
            // A jagged array, and this includes byte[][], has no mapping for an element position.
            case BsonMapping.Binary:
            case BsonMapping.Array:
                scope.Unsupported(sb, indent, "nested arrays are not supported", Display(type));
                return;

            case BsonMapping.String:
                Add($"{SizeType}.String({accessor})");
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                Add($"Measure{methodName}({accessor}, {SizeTableParameter})");
                return;

            // ReadOnlyMemory<byte> has no write overload without a name.
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

        // BsonReader is a ref struct. It goes by reference, because a copy would read the same
        // bytes a second time from the position that this method received.
        sb.AppendLine($"    private {typeName}? Read{methodName}(ref BsonReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("        reader.ReadStartDocument();");
        sb.AppendLine($"        var result = Read{methodName}Inner(ref reader);");
        sb.AppendLine("        reader.ReadEndDocument();");
        sb.AppendLine("        return result;");
        sb.AppendLine("#nullable restore");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    private {typeName} Read{methodName}Inner(ref BsonReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("#nullable disable");

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

        // Both forms below are a list of the local variables from above, with a comma between
        // them and one on each line. Only the name of the target on each line is different.
        void AppendPropertyList(Func<PropertyInfo, string> line)
        {
            var first = true;
            foreach (var property in type.Properties)
            {
                if (!first)
                    sb.AppendLine(",");

                first = false;
                sb.Append($"            {line(property)}");
            }

            sb.AppendLine();
        }

        if (type.IsRecord)
        {
            sb.AppendLine($"        return new {typeName}(");
            AppendPropertyList(p => $"_{p.Name}");
            sb.AppendLine("        );");
        }
        else if (type.Properties.FirstOrDefault(p => !p.IsSettable) is { } readOnlyProperty)
        {
            // An object initializer cannot set this property. Generated code for it gives the
            // user many CS0200 errors that point at generated code. Report MINIBSON001 instead.
            ScopeFor(diagnostics, type, readOnlyProperty).Unsupported(
                sb,
                "        ",
                $"property '{readOnlyProperty.Name}' has no public setter, so the type cannot be deserialized",
                Display(type.FullyQualifiedName));
            sb.AppendLine("        return default;");
        }
        else
        {
            sb.AppendLine($"        return new {typeName}");
            sb.AppendLine("        {");
            AppendPropertyList(p => $"{p.Name} = _{p.Name}");
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
                sb.AppendLine($"{indent}    _{name} = reader.ReadBinaryArray(out _);");
                return;

            case BsonMapping.BinaryMemory:
                sb.AppendLine($"{indent}    _{name} = reader.ReadBinaryMemory(out _);");
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
            // A jagged array, and this includes byte[][], has no mapping for an element position.
            case BsonMapping.Binary:
            case BsonMapping.Array:
                scope.Unsupported(sb, indent, "nested arrays are not supported", Display(type));
                return;

            case BsonMapping.Nested:
                var methodName = GetSafeMethodName(mapping.NestedType!);
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    reader.ReadStartNestedDocument();");
                sb.AppendLine($"{indent}    list.Add(Read{methodName}Inner(ref reader));");
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
                sb.AppendLine($"{indent}    _{name} = Read{methodName}Inner(ref reader);");
                sb.AppendLine($"{indent}    reader.ReadEndDocument();");
                sb.AppendLine($"{indent}}}");
                return;

            // GenerateReadProperty finds these types before it calls this method.
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
        sb.AppendLine("    /// The fallback for each member with a MINIBSON001 diagnostic. Code reaches this");
        sb.AppendLine("    /// method only if you change the severity of that diagnostic or suppress it.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static void ThrowUnsupported(string member, string type)");
        sb.AppendLine("        => throw new NotSupportedException(");
        sb.AppendLine("            $\"MiniBson cannot serialize '{member}': type '{type}' is not supported \" +");
        sb.AppendLine("            \"by the source generator (MINIBSON001).\");");
    }

    private static void GenerateGetSerializedSizeMethod(StringBuilder sb, EquatableList<TypeInfo> types)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Returns the number of bytes that <see cref=\"Serialize\"/> writes for this value.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <remarks>");
        sb.AppendLine("    /// The number is exact and not an estimate, but only if each property returns the same");
        sb.AppendLine("    /// value two times. It is the same length that <see cref=\"Serialize\"/> computes, so it");
        sb.AppendLine("    /// also sizes a destination buffer exactly.");
        sb.AppendLine("    /// </remarks>");
        sb.AppendLine("    public int GetSerializedSize(object input)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (input is null) throw new ArgumentNullException(nameof(input));");
        sb.AppendLine("        var inputType = input.GetType();");

        // BsonSizeTable.None measures and records nothing, because no write pass follows it.
        EmitTypeDispatch(
            sb,
            types,
            "inputType",
            type => $"return Measure{GetSafeMethodName(type)}(({type.FullyQualifiedName})input, {SizeTableType}.None);",
            "serialization");

        sb.AppendLine("    }");
    }

    private static void GenerateSerializeMethod(StringBuilder sb, EquatableList<TypeInfo> types)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Serializes the given object to the BSON format.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public void Serialize(object input, BsonWriter writer)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (input is null) throw new ArgumentNullException(nameof(input));");
        sb.AppendLine("        if (writer is null) throw new ArgumentNullException(nameof(writer));");
        sb.AppendLine("        var inputType = input.GetType();");

        EmitTypeDispatch(
            sb,
            types,
            "inputType",
            type => $"Write{GetSafeMethodName(type)}(writer, ({type.FullyQualifiedName})input);",
            "serialization");

        sb.AppendLine("    }");
    }

    private static void GenerateDeserializeMethod(StringBuilder sb, EquatableList<TypeInfo> types)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Deserializes BSON data to an object of the given type.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public object? Deserialize(ref BsonReader reader, Type type)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (type is null) throw new ArgumentNullException(nameof(type));");

        EmitTypeDispatch(
            sb,
            types,
            "type",
            type => $"return Read{GetSafeMethodName(type)}(ref reader);",
            "deserialization");

        sb.AppendLine("    }");
    }

    /// <summary>
    /// Writes the if/else chain that compares a runtime type with each registered model. The
    /// three public methods dispatch in the same manner, so they use this one method. Thus each
    /// method does not have its own copy of the chain and its own errors.
    /// </summary>
    /// <param name="selector">
    /// The expression that gives the <c>Type</c> to compare. The failure message also uses it
    /// to name that type.
    /// </param>
    /// <param name="body">The statement to write for a model that matches.</param>
    private static void EmitTypeDispatch(
        StringBuilder sb,
        EquatableList<TypeInfo> types,
        string selector,
        Func<TypeInfo, string> body,
        string operation)
    {
        var first = true;
        foreach (var type in types)
        {
            sb.AppendLine($"        {(first ? "if" : "else if")} ({selector} == typeof({type.FullyQualifiedName}))");
            sb.AppendLine($"            {body(type)}");
            first = false;
        }

        // A context with no registered type must still compile, so the throw is alone here.
        var indent = first ? "        " : "            ";
        if (!first)
            sb.AppendLine("        else");

        sb.AppendLine($"{indent}throw new NotSupportedException($\"Type {{{selector}}} is not supported for {operation}.\");");
    }

    /// <summary>
    /// The public instance properties of a type and its base types that the generator can read.
    /// The most derived type comes first.
    /// </summary>
    /// <remarks>
    /// You must not change this order. It is the order of the elements on the wire, so a
    /// different order changes the bytes for each model with a base class. If a name occurs two
    /// times, the first declaration wins. Thus a <c>new</c> property replaces the property that
    /// it hides.
    /// </remarks>
    private static IEnumerable<IPropertySymbol> GetAllProperties(INamedTypeSymbol type)
    {
        var properties = new List<IPropertySymbol>();
        var seenPropertyNames = new HashSet<string>();

        for (var current = type;
             current != null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } property &&
                    property.GetMethod != null &&
                    seenPropertyNames.Add(property.Name))
                {
                    properties.Add(property);
                }
            }
        }

        return properties;
    }

    /// <summary>
    /// The part of an identifier that names one model type in the generated members. It comes
    /// from the fully qualified name and not from the simple name. Two models with the name
    /// <c>Order</c> in different namespaces are both legal, and a simple name would give the
    /// same methods two times in one class.
    /// </summary>
    private static string GetSafeMethodName(TypeInfo type)
    {
        var name = Display(type.FullyQualifiedName);
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        return builder.ToString();
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
        var nullableValueType = symbol as INamedTypeSymbol;
        if (nullableValueType is not { IsGenericType: true } ||
            nullableValueType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        {
            nullableValueType = null;
        }

        var isNullable = nullableValueType is not null ||
                         symbol.NullableAnnotation == NullableAnnotation.Annotated;

        TypeRefInfo? nullableUnderlying = null;
        if (nullableValueType is not null)
        {
            nullableUnderlying = ExtractTypeRefInfo(nullableValueType.TypeArguments[0], visited);
        }

        TypeRefInfo? arrayElement = null;
        if (symbol is IArrayTypeSymbol arrayType)
        {
            arrayElement = ExtractTypeRefInfo(arrayType.ElementType, visited);
        }

        SpecialType? enumUnderlying = null;
        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            enumUnderlying = enumType.EnumUnderlyingType?.SpecialType;
        }

        // An array symbol is never an INamedTypeSymbol. Thus an array stops here, and it needs
        // no test of its own.
        TypeInfo? nestedTypeInfo = null;
        if (symbol is INamedTypeSymbol namedType &&
            !IsPrimitiveType(symbol) &&
            // The decimal type has no BSON mapping here. As an ordinary model, it would give an
            // empty document and no error. Thus it continues to MINIBSON001.
            symbol.SpecialType != SpecialType.System_Decimal &&
            nullableValueType is null)
        {
            nestedTypeInfo = ExtractTypeInfo(namedType, visited);
        }

        return new TypeRefInfo(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            symbol.Name,
            symbol.SpecialType,
            symbol.IsValueType,
            isNullable,
            enumUnderlying,
            arrayElement,
            nullableUnderlying,
            nestedTypeInfo);
    }
}
