using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MiniBson.Generator;

namespace MiniBson.Tests;

/// <summary>
/// Unsupported types must fail the build with MINIBSON001 rather than silently
/// round-tripping as an empty value. These run the generator in-process, because a
/// model that trips the diagnostic cannot be compiled into this test project.
/// </summary>
[TestClass]
public sealed class BsonGeneratorDiagnosticTests
{
    private const string ModelPath = "Models.cs";

    private static string BuildSource([StringSyntax("csharp")] string modelSource) =>
        $$"""
          using System;
          using MiniBson;

          namespace GeneratorTestModels;

          {{modelSource}}
          """;

    private static ImmutableArray<Diagnostic> RunGenerator([StringSyntax("csharp")] string modelSource)
    {
        Run(BuildSource(modelSource), out _, out var diagnostics);
        return diagnostics;
    }

    /// <summary>
    /// Diagnostics from compiling the generated code, rather than from producing it. Some
    /// mistakes only surface here, as errors inside a file the user cannot edit.
    /// </summary>
    private static ImmutableArray<Diagnostic> CompileGenerated([StringSyntax("csharp")] string source)
    {
        Run(source, out var updated, out _);
        return updated.GetDiagnostics();
    }

    private static void Run(
        string source,
        out Compilation updated,
        out ImmutableArray<Diagnostic> generatorDiagnostics)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location)
            .Append(typeof(BsonSerializableAttribute).Assembly.Location)
            .Append(typeof(object).Assembly.Location)
            .Distinct()
            .Select(location => (MetadataReference)MetadataReference.CreateFromFile(location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "GeneratorDiagnosticTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: ModelPath)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create(new BsonSerializerGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out updated, out generatorDiagnostics);
    }

    private static Diagnostic SingleUnsupported([StringSyntax("csharp")] string modelSource)
    {
        var diagnostics = RunGenerator(modelSource);

        Assert.AreEqual(1, diagnostics.Length,
            "Expected exactly one diagnostic, got: " + string.Join("; ", diagnostics));
        Assert.AreEqual("MINIBSON001", diagnostics[0].Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostics[0].Severity);
        return diagnostics[0];
    }

    [TestMethod]
    public void Decimal_IsReported()
    {
        var diagnostic = SingleUnsupported(
            """
            public class Model
            {
                public decimal Money { get; set; }
            }

            [BsonSerializable(typeof(Model))]
            public partial class Context;
            """);

        var message = diagnostic.GetMessage();
        StringAssert.Contains(message, "Model.Money");
        StringAssert.Contains(message, "decimal");
    }

    [TestMethod]
    public void JaggedArray_IsReported()
    {
        var diagnostic = SingleUnsupported(
            """
            public class Model
            {
                public int[][] Jagged { get; set; } = [];
            }

            [BsonSerializable(typeof(Model))]
            public partial class Context;
            """);

        var message = diagnostic.GetMessage();
        StringAssert.Contains(message, "Model.Jagged[]");
        StringAssert.Contains(message, "nested arrays are not supported");
    }

    [TestMethod]
    public void JaggedBinaryArray_IsReported()
    {
        var diagnostic = SingleUnsupported(
            """
            public class Model
            {
                public byte[][] Chunks { get; set; } = [];
            }

            [BsonSerializable(typeof(Model))]
            public partial class Context;
            """);

        StringAssert.Contains(diagnostic.GetMessage(), "Model.Chunks[]");
    }

    [TestMethod]
    public void GetOnlyProperty_IsReported()
    {
        var diagnostic = SingleUnsupported(
            """
            public class Model
            {
                public int Value { get; set; }
                public int Doubled => Value * 2;
            }

            [BsonSerializable(typeof(Model))]
            public partial class Context;
            """);

        var message = diagnostic.GetMessage();
        StringAssert.Contains(message, "Doubled");
        StringAssert.Contains(message, "no public setter");
    }

    [TestMethod]
    public void UnsupportedNestedType_IsReportedOnce()
    {
        // TimeSpan is all get-only properties; it used to emit a wall of CS0200s
        // pointing into generated code instead of one actionable diagnostic.
        var diagnostic = SingleUnsupported(
            """
            public class Model
            {
                public TimeSpan Span { get; set; }
            }

            [BsonSerializable(typeof(Model))]
            public partial class Context;
            """);

        StringAssert.Contains(diagnostic.GetMessage(), "no public setter");
    }

    [TestMethod]
    public void DiagnosticPointsAtTheOffendingProperty()
    {
        const string model = """
                             public class Model
                             {
                                 public int Fine { get; set; }
                                 public decimal Money { get; set; }
                             }

                             [BsonSerializable(typeof(Model))]
                             public partial class Context;
                             """;

        var diagnostic = SingleUnsupported(model);
        var span = diagnostic.Location.GetLineSpan();

        Assert.AreEqual(ModelPath, span.Path);

        var line = BuildSource(model).Split('\n')[span.StartLinePosition.Line];
        StringAssert.Contains(line, "Money", "Diagnostic should point at the offending property.");
    }

    [TestMethod]
    public void SupportedModel_ReportsNothing()
    {
        var diagnostics = RunGenerator(
            """
            public enum Status : ushort { A = 1, B = 2 }

            public class Inner
            {
                public Guid Id { get; set; }
                public ulong Big { get; set; }
            }

            public record Rec(Status Status, ushort[] Values);

            public class Model
            {
                public Status Status { get; set; }
                public Status? MaybeStatus { get; set; }
                public Status[] Statuses { get; set; } = [];
                public byte Small { get; set; }
                public uint Medium { get; set; }
                public Guid[] Ids { get; set; } = [];
                public byte[] Blob { get; set; } = [];
                public ReadOnlyMemory<byte> Memory { get; set; }
                public Inner? Nested { get; set; }
                public Inner[] Many { get; set; } = [];
                public Rec? Record { get; set; }
            }

            [BsonSerializable(typeof(Model))]
            public partial class Context;
            """);

        Assert.AreEqual(0, diagnostics.Length,
            "Expected no diagnostics, got: " + string.Join("; ", diagnostics));
    }

    /// <summary>
    /// Only <c>System.Guid</c> maps to the BSON UUID subtype. A user type that merely shares
    /// the simple name is an ordinary model, and claiming it emitted a <c>WriteGuid</c> call
    /// that does not compile.
    /// </summary>
    [TestMethod]
    public void UserTypeNamedGuidIsTreatedAsANestedModel()
    {
        var errors = CompileGenerated(
            """
            using MiniBson;

            namespace App
            {
                public class Guid
                {
                    public int Value { get; set; }
                }

                public class Model
                {
                    public Guid Custom { get; set; } = new();
                }

                [BsonSerializable(typeof(Model))]
                public partial class Context;
            }
            """)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.AreEqual(0, errors.Length,
            "Generated code should compile, got: " + string.Join("; ", errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// Two models can legally share a simple name. Generated helpers all land in one partial
    /// class, so naming them after the simple name emits the same member twice — a CS0111 in
    /// a file the user cannot edit and did not write.
    /// </summary>
    [TestMethod]
    public void ModelsSharingASimpleNameCompile()
    {
        var errors = CompileGenerated(
            """
            using System;
            using MiniBson;

            namespace Ordering
            {
                public class Order
                {
                    public string Reference { get; set; } = "";
                    public Line[] Items { get; set; } = [];
                }

                public class Line
                {
                    public int Quantity { get; set; }
                }
            }

            namespace Billing
            {
                public class Order
                {
                    public string Reference { get; set; } = "";
                    public Line[] Items { get; set; } = [];
                }

                public class Line
                {
                    public double Amount { get; set; }
                }
            }

            namespace App
            {
                [BsonSerializable(typeof(Ordering.Order))]
                [BsonSerializable(typeof(Billing.Order))]
                public partial class Context;
            }
            """)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.AreEqual(0, errors.Length,
            "Generated code should compile, got: " + string.Join("; ", errors.Select(e => e.ToString())));
    }
}
