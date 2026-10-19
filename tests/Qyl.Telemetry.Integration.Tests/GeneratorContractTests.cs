using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class GeneratorContractTests(TestContext testContext)
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    [TestMethod]
    public void Changed_dependency_signature_is_diagnosed_without_emitting_an_interceptor()
    {
        var result = Run("""
// A call site that names a declared integration receiver and method but does not fit the declared
// shape. QylUnmatchedCommand derives from System.Data.Common.DbCommand, so the receiver matches, and
// the method name ExecuteScalar matches the DbCommand intercept declaration -- but the signature
// takes an int and returns string, which the DbCommand shape does not describe.
//
// The generator must emit NO interceptor for it and report exactly one QYL1001 instead. This is the
// failure mode a library major introduces when it changes an intercepted signature: silence here
// would mean instrumentation disappearing with no signal, and emitting an interceptor anyway would
// break the consumer's build.
internal static class ShapeMismatchProbe
{
    internal static string Unmatched(QylUnmatchedCommand command)
        => command.ExecuteScalar(1);
}

internal sealed class QylUnmatchedCommand : SnapshotCommand
{
    internal string ExecuteScalar(int id)
        => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
""", commandSource: true).Result;
        var diagnostic = Assert.ContainsSingle(result.Diagnostics);
        Assert.AreEqual("QYL1001", diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("QylUnmatchedCommand.ExecuteScalar", diagnostic.GetMessage());
        Assert.Contains("not instrumented", diagnostic.GetMessage());
        Assert.IsFalse(result.GeneratedSources.Any(static source => source.HintName == "QylAutoInstrumentation.Interceptors.g.cs"));
    }

    [TestMethod]
    public void GenAi_libraries_without_their_opt_ins_report_each_missing_activation()
    {
        var result = Run("""
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

// QYL1002's three GenAI rows: each library is used here and never opted in, so the generator reports
// one warning per row. Nothing calls these methods; they only have to compile.
internal static class GenAiOptInsProbe
{
    internal static object Chat(IChatClient client) => client.GetResponseAsync("probe");

    internal static object Agent(AIAgent agent) => agent.RunAsync("probe");

    internal static object Workflow(WorkflowBuilder builder) => builder.Build();
}
""", expectedGeneratorDiagnostics: ["QYL1002", "QYL1002", "QYL1002"]).Result;
        Assert.HasCount(3, result.Diagnostics);
        foreach (var library in new[] { "Microsoft.Extensions.AI", "Microsoft.Agents.AI", "Microsoft.Agents.AI.Workflows" })
        {
            var diagnostic = Assert.ContainsSingle(result.Diagnostics.Where(d => d.GetMessage().StartsWith("qyl subscribes " + library + "'s ActivitySource", StringComparison.Ordinal)));
            Assert.AreEqual("QYL1002", diagnostic.Id);
            Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        }
    }

    [TestMethod]
    public void Valid_db_command_emits_compilable_interceptor_for_the_current_runtime_abi()
    {
        var (_, compilation) = Run("""
internal static class Consumer
{
    internal static object? Run(SnapshotCommand command) => command.ExecuteScalar();
}
""", commandSource: true);
        AssertIntercepted(compilation, "ExecuteScalar");
    }

    [TestMethod]
    [DataRow(false, ArgumentsDisplayName = "Produce with optional callback")]
    [DataRow(true, ArgumentsDisplayName = "ProduceAsync with optional cancellation")]
    public void Nullable_Kafka_receiver_and_optional_arguments_compile_without_warnings(bool asynchronous)
    {
        var method = asynchronous ? "ProduceAsync" : "Produce";
        var returnType = asynchronous ? "Task<DeliveryResult<string?, string?>>" : "void";
        var (_, compilation) = Run($$"""
using Confluent.Kafka;
using System.Threading.Tasks;

internal static class Consumer
{
    internal static {{returnType}} Send(
        IProducer<string?, string?> producer, Message<string?, string?> message)
        => producer.{{method}}("topic", message);
}
""");
        var (original, interceptor) = AssertIntercepted(compilation, method);
        Assert.IsTrue(SymbolEqualityComparer.IncludeNullability.Equals(
            original.ContainingType.WithNullableAnnotation(NullableAnnotation.NotAnnotated), interceptor.Parameters[0].Type),
            "The receiver must preserve the original type arguments and their nullability.");
    }

    [TestMethod]
    [DataRow("string?", ArgumentsDisplayName = "nullable reference")]
    [DataRow("string?[,]?", ArgumentsDisplayName = "nullable multidimensional array")]
    [DataRow("string?[]?[]?", ArgumentsDisplayName = "nullable jagged array")]
    public void Nullable_parameters_and_returns_preserve_the_bound_signature(string type)
    {
        var (_, compilation) = Run(WcfConsumer(type));
        var (original, interceptor) = AssertIntercepted(compilation, "Echo");
        Assert.IsTrue(SymbolEqualityComparer.IncludeNullability.Equals(original.ReturnType, interceptor.ReturnType),
            "The return type must preserve explicit nullable annotations.");
        Assert.IsTrue(SymbolEqualityComparer.IncludeNullability.Equals(original.Parameters[0].Type, interceptor.Parameters[^1].Type),
            "The parameter type must preserve explicit nullable annotations and array structure.");
    }

    [TestMethod]
    [DataRow("[Obsolete]", "CS0612", false)]
    [DataRow("[Obsolete(\"Use replacement\")]", "CS0618", false)]
    [DataRow("[Obsolete(\"Use replacement\", DiagnosticId = \"LEGACY001\")]", "LEGACY001", true)]
    public void Obsolete_forwarding_respects_the_callers_local_warning_suppression(
        string attribute, string diagnostic, bool asynchronous)
    {
        var (_, compilation) = Run(WcfConsumer("string?", attribute, diagnostic, asynchronous));
        AssertIntercepted(compilation, "Echo");
    }

    [TestMethod]
    public void Obsolete_warning_remains_at_the_original_call_when_the_caller_does_not_suppress_it()
    {
        var (_, compilation) = Run(WcfConsumer("string?", "[Obsolete(\"Use replacement\")]"),
            expectedCompilerDiagnostics: ["CS0618"]);
        var diagnostic = Assert.ContainsSingle(CompilerProblems(compilation));
        Assert.AreEqual("Consumer.cs", diagnostic.Location.SourceTree?.FilePath);
        Assert.IsTrue(diagnostic.IsWarningAsError);
        AssertIntercepted(compilation, "Echo");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Obsolete_db_forwarding_respects_the_callers_local_warning_suppression(bool asynchronous)
    {
        var returnType = asynchronous ? "System.Threading.Tasks.Task<object?>" : "object?";
        var method = asynchronous ? "ExecuteScalarAsync" : "ExecuteScalar";
        var body = asynchronous ? "System.Threading.Tasks.Task.FromResult<object?>(null)" : "null";
        var (_, compilation) = Run($$"""
internal sealed class LegacyCommand : SnapshotCommand
{
    [System.Obsolete("Use replacement")]
    public new {{returnType}} {{method}}() => {{body}};
}
internal static class Consumer
{
#pragma warning disable CS0618
    internal static {{returnType}} Run(LegacyCommand command) => command.{{method}}();
#pragma warning restore CS0618
}
""", commandSource: true);
        AssertIntercepted(compilation, method);
    }

    private static string WcfConsumer(string type, string attribute = "", string suppressedDiagnostic = "", bool asynchronous = false)
    {
        var returnType = asynchronous ? $"System.Threading.Tasks.Task<{type}>" : type;
        var body = asynchronous ? "System.Threading.Tasks.Task.FromResult(value)" : "value";
        return $$"""
using System;
using System.ServiceModel;

[ServiceContract]
internal interface IContract
{
    [OperationContract]
    {{returnType}} Echo({{type}} value);
}
internal sealed class Client : ClientBase<IContract>, IContract
{
    {{attribute}}
    public {{returnType}} Echo({{type}} value) => {{body}};
}
internal static class Consumer
{
    {{(suppressedDiagnostic.Length == 0 ? "" : "#pragma warning disable " + suppressedDiagnostic)}}
    internal static {{returnType}} Run(Client client) => client.Echo(default);
    {{(suppressedDiagnostic.Length == 0 ? "" : "#pragma warning restore " + suppressedDiagnostic)}}
}
""";
    }

    private (IMethodSymbol Original, IMethodSymbol Interceptor) AssertIntercepted(CSharpCompilation compilation, string method)
    {
        var tree = Assert.ContainsSingle(compilation.SyntaxTrees.Where(static tree => tree.FilePath == "Consumer.cs"));
        var invocation = Assert.ContainsSingle(tree.GetRoot(testContext.CancellationToken).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == method));
        var model = compilation.GetSemanticModel(tree);
        var original = Assert.IsInstanceOfType<IMethodSymbol>(model.GetSymbolInfo(invocation, testContext.CancellationToken).Symbol);
        var interceptor = model.GetInterceptorMethod(invocation, testContext.CancellationToken);
        Assert.IsNotNull(interceptor);
        return (original, interceptor);
    }

    private Diagnostic[] CompilerProblems(Compilation compilation) =>
        compilation.GetDiagnostics(testContext.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();

    private void AssertCompilerDiagnostics(Compilation compilation, string[] expected)
    {
        var diagnostics = CompilerProblems(compilation);
        Assert.AreSequenceEqual(expected.Order(StringComparer.Ordinal).ToArray(),
            diagnostics.Select(static diagnostic => diagnostic.Id).Order(StringComparer.Ordinal).ToArray(),
            string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private (GeneratorRunResult Result, CSharpCompilation Compilation) Run(string source, bool commandSource = false,
        string[]? expectedCompilerDiagnostics = null, string[]? expectedGeneratorDiagnostics = null)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, ParseOptions, "Consumer.cs", cancellationToken: testContext.CancellationToken) };
        if (commandSource)
            trees.Add(CSharpSyntaxTree.ParseText("using System; using System.Threading; using System.Threading.Tasks;\n" + File.ReadAllText(Repo.Root / "tests/Qyl.Telemetry.AutoInstrumentation.SourceGenerators.Snapshots/Fixture/SnapshotCommand.cs"), ParseOptions, "SnapshotCommand.cs", cancellationToken: testContext.CancellationToken));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Append(typeof(Confluent.Kafka.IProducer<,>).Assembly.Location)
            .Append(typeof(System.ServiceModel.ClientBase<>).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GeneratorConsumer", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        AssertCompilerDiagnostics(compilation, expectedCompilerDiagnostics ?? []);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, testContext.CancellationToken);
        AssertCompilerDiagnostics(updated, expectedCompilerDiagnostics ?? []);
        var result = Assert.ContainsSingle(driver.GetRunResult().Results);
        var generatorProblems = result.Diagnostics
            .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();
        Assert.AreSequenceEqual((expectedGeneratorDiagnostics ?? []).Order(StringComparer.Ordinal).ToArray(),
            generatorProblems.Select(static diagnostic => diagnostic.Id).Order(StringComparer.Ordinal).ToArray(),
            string.Join(Environment.NewLine, generatorProblems.Select(static diagnostic => diagnostic.ToString())));
        return (result, (CSharpCompilation)updated);
    }
}
