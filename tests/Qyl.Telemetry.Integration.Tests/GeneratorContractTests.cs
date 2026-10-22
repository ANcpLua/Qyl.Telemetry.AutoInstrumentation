using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>
/// The source generator against consumer compilations. Each scenario is compiled once, before and after
/// generation, and the tests read its compilations, its generator result and the interceptor bound to the
/// consumer's call. A changed dependency signature is the failure mode a library major introduces: the
/// generator must emit no interceptor for it and report exactly one QYL1001 instead, since silence would
/// mean instrumentation disappearing with no signal and an interceptor anyway would break the build.
/// </summary>
[TestClass]
public sealed class GeneratorContractTests
{
    private const string ChangedSignature = "changed-dependency-signature";
    private const string GenAiWithoutOptIns = "genai-without-opt-ins";
    private const string ValidDbCommand = "valid-db-command";
    private const string KafkaProduce = "kafka-produce-optional-callback";
    private const string KafkaProduceAsync = "kafka-produce-async-optional-cancellation";
    private const string WcfNullableReference = "wcf-nullable-reference";
    private const string WcfNullableMultidimensional = "wcf-nullable-multidimensional-array";
    private const string WcfNullableJagged = "wcf-nullable-jagged-array";
    private const string ObsoleteSuppressed = "obsolete-suppressed";
    private const string ObsoleteMessageSuppressed = "obsolete-message-suppressed";
    private const string ObsoleteDiagnosticIdSuppressedAsync = "obsolete-diagnostic-id-suppressed-async";
    private const string ObsoleteUnsuppressed = "obsolete-unsuppressed";
    private const string ObsoleteDbSuppressed = "obsolete-db-suppressed";
    private const string ObsoleteDbSuppressedAsync = "obsolete-db-suppressed-async";
    private const string InterceptorsSource = "QylAutoInstrumentation.Interceptors.g.cs";

    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    private static readonly Dictionary<string, Scenario> Scenarios = new(StringComparer.Ordinal)
    {
        [ChangedSignature] = new(ShapeMismatchSource, CommandSource: true),
        [GenAiWithoutOptIns] = new(GenAiSource, GeneratorDiagnostics: ["QYL1002", "QYL1002", "QYL1002"]),
        [ValidDbCommand] = new(ValidDbSource, CommandSource: true, InterceptedMethod: "ExecuteScalar"),
        [KafkaProduce] = new(KafkaConsumer(asynchronous: false), InterceptedMethod: "Produce"),
        [KafkaProduceAsync] = new(KafkaConsumer(asynchronous: true), InterceptedMethod: "ProduceAsync"),
        [WcfNullableReference] = new(WcfConsumer("string?"), InterceptedMethod: "Echo"),
        [WcfNullableMultidimensional] = new(WcfConsumer("string?[,]?"), InterceptedMethod: "Echo"),
        [WcfNullableJagged] = new(WcfConsumer("string?[]?[]?"), InterceptedMethod: "Echo"),
        [ObsoleteSuppressed] = new(WcfConsumer("string?", "[Obsolete]", "CS0612"), InterceptedMethod: "Echo"),
        [ObsoleteMessageSuppressed] = new(WcfConsumer("string?", "[Obsolete(\"Use replacement\")]", "CS0618"), InterceptedMethod: "Echo"),
        [ObsoleteDiagnosticIdSuppressedAsync] = new(
            WcfConsumer("string?", "[Obsolete(\"Use replacement\", DiagnosticId = \"LEGACY001\")]", "LEGACY001", asynchronous: true), InterceptedMethod: "Echo"),
        [ObsoleteUnsuppressed] = new(WcfConsumer("string?", "[Obsolete(\"Use replacement\")]"), CompilerDiagnostics: ["CS0618"], InterceptedMethod: "Echo"),
        [ObsoleteDbSuppressed] = new(ObsoleteDbConsumer(asynchronous: false), CommandSource: true, InterceptedMethod: "ExecuteScalar"),
        [ObsoleteDbSuppressedAsync] = new(ObsoleteDbConsumer(asynchronous: true), CommandSource: true, InterceptedMethod: "ExecuteScalarAsync"),
    };

    private static readonly ConcurrentDictionary<string, Lazy<GeneratorRun>> Runs = new(StringComparer.Ordinal);

    public static IEnumerable<object[]> AllScenarios => Scenarios.Keys.Select(static name => new object[] { name });

    public static IEnumerable<object[]> InterceptedScenarios =>
        Scenarios.Where(static pair => pair.Value.InterceptedMethod is not null).Select(static pair => new object[] { pair.Key });

    [TestMethod]
    [DynamicData(nameof(AllScenarios))]
    public void ConsumerSource_EachScenario_CompilesWithOnlyTheExpectedDiagnostics(string scenario)
    {
        var problems = Problems(Run(scenario).Input);

        Assert.AreSequenceEqual(Expected(Scenarios[scenario].CompilerDiagnostics), Ids(problems), Describe(problems));
    }

    [TestMethod]
    [DynamicData(nameof(AllScenarios))]
    public void GeneratedConsumer_EachScenario_CompilesWithOnlyTheExpectedDiagnostics(string scenario)
    {
        var problems = Problems(Run(scenario).Output);

        Assert.AreSequenceEqual(Expected(Scenarios[scenario].CompilerDiagnostics), Ids(problems), Describe(problems));
    }

    [TestMethod]
    [DynamicData(nameof(AllScenarios))]
    public void Generator_EachScenario_RunsOnce(string scenario) =>
        Assert.HasCount(1, Run(scenario).Results);

    [TestMethod]
    [DynamicData(nameof(AllScenarios))]
    public void Generator_EachScenario_ReportsOnlyTheExpectedProblems(string scenario)
    {
        var problems = Run(scenario).Result.Diagnostics.Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();

        Assert.AreSequenceEqual(Expected(Scenarios[scenario].GeneratorDiagnostics), Ids(problems), Describe(problems));
    }

    [TestMethod]
    [DynamicData(nameof(InterceptedScenarios))]
    public void Generator_InterceptableCall_BindsAnInterceptor(string scenario) =>
        Assert.IsNotNull(Interception(scenario).Interceptor);

    [TestMethod]
    public void Generator_ChangedDependencySignature_ReportsOneQyl1001Info()
    {
        var diagnostic = Assert.ContainsSingle(Run(ChangedSignature).Result.Diagnostics);

        Assert.AreEqual(("QYL1001", DiagnosticSeverity.Info), (diagnostic.Id, diagnostic.Severity));
    }

    [TestMethod]
    [DataRow("QylUnmatchedCommand.ExecuteScalar")]
    [DataRow("not instrumented")]
    public void Generator_ChangedDependencySignature_NamesTheUninstrumentedCall(string fragment) =>
        Assert.Contains(fragment, Run(ChangedSignature).Result.Diagnostics.Single().GetMessage(), StringComparison.Ordinal);

    [TestMethod]
    public void Generator_ChangedDependencySignature_EmitsNoInterceptorSource() =>
        Assert.DoesNotContain(static source => source.HintName == InterceptorsSource, Run(ChangedSignature).Result.GeneratedSources);

    [TestMethod]
    public void Generator_GenAiLibrariesWithoutOptIns_ReportsThreeDiagnostics() =>
        Assert.HasCount(3, Run(GenAiWithoutOptIns).Result.Diagnostics);

    [TestMethod]
    [DataRow("Microsoft.Extensions.AI")]
    [DataRow("Microsoft.Agents.AI")]
    [DataRow("Microsoft.Agents.AI.Workflows")]
    public void Generator_GenAiLibraryWithoutOptIn_ReportsOneQyl1002Error(string library)
    {
        var diagnostic = Assert.ContainsSingle(Run(GenAiWithoutOptIns).Result.Diagnostics
            .Where(diagnostic => diagnostic.GetMessage().StartsWith("qyl subscribes " + library + "'s ActivitySource", StringComparison.Ordinal)));

        Assert.AreEqual(("QYL1002", DiagnosticSeverity.Error), (diagnostic.Id, diagnostic.Severity));
    }

    [TestMethod]
    [DataRow(KafkaProduce)]
    [DataRow(KafkaProduceAsync)]
    public void KafkaInterceptor_NullableTypeArguments_KeepsTheReceiverNullability(string scenario)
    {
        var (original, interceptor) = Interception(scenario);

        Assert.IsTrue(SymbolEqualityComparer.IncludeNullability.Equals(
            original?.ContainingType.WithNullableAnnotation(NullableAnnotation.NotAnnotated), interceptor?.Parameters[0].Type));
    }

    [TestMethod]
    [DataRow(WcfNullableReference)]
    [DataRow(WcfNullableMultidimensional)]
    [DataRow(WcfNullableJagged)]
    public void WcfInterceptor_NullableSignature_KeepsTheReturnNullability(string scenario)
    {
        var (original, interceptor) = Interception(scenario);

        Assert.IsTrue(SymbolEqualityComparer.IncludeNullability.Equals(original?.ReturnType, interceptor?.ReturnType));
    }

    [TestMethod]
    [DataRow(WcfNullableReference)]
    [DataRow(WcfNullableMultidimensional)]
    [DataRow(WcfNullableJagged)]
    public void WcfInterceptor_NullableSignature_KeepsTheParameterNullabilityAndArrayShape(string scenario)
    {
        var (original, interceptor) = Interception(scenario);

        Assert.IsTrue(SymbolEqualityComparer.IncludeNullability.Equals(original?.Parameters[0].Type, interceptor?.Parameters[^1].Type));
    }

    [TestMethod]
    public void ObsoleteCall_CallerDoesNotSuppress_WarnsInTheConsumerFile()
    {
        var diagnostic = Assert.ContainsSingle(Problems(Run(ObsoleteUnsuppressed).Output));

        Assert.AreEqual("Consumer.cs", diagnostic.Location.SourceTree?.FilePath);
    }

    [TestMethod]
    public void ObsoleteCall_CallerDoesNotSuppress_ReportsTheWarningAsError() =>
        Assert.IsTrue(Problems(Run(ObsoleteUnsuppressed).Output).Single().IsWarningAsError);

    private const string ShapeMismatchSource = """
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
""";

    private const string GenAiSource = """
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
""";

    private const string ValidDbSource = """
internal static class Consumer
{
    internal static object? Run(SnapshotCommand command) => command.ExecuteScalar();
}
""";

    private sealed record Scenario(
        string Source,
        bool CommandSource = false,
        string[]? CompilerDiagnostics = null,
        string[]? GeneratorDiagnostics = null,
        string? InterceptedMethod = null);

    /// <summary>A scenario's compilation before and after generation, and the generator's results.</summary>
    private sealed record GeneratorRun(CSharpCompilation Input, CSharpCompilation Output, IReadOnlyList<GeneratorRunResult> Results)
    {
        public GeneratorRunResult Result => Results.Single();
    }

    private static string[] Expected(string[]? ids) => [.. (ids ?? []).Order(StringComparer.Ordinal)];

    private static string[] Ids(IEnumerable<Diagnostic> diagnostics) => [.. diagnostics.Select(static diagnostic => diagnostic.Id).Order(StringComparer.Ordinal)];

    private static string Describe(IEnumerable<Diagnostic> diagnostics) => string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));

    private static Diagnostic[] Problems(Compilation compilation) =>
    [
        .. compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
    ];

    private static (IMethodSymbol? Original, IMethodSymbol? Interceptor) Interception(string scenario)
    {
        var method = Scenarios[scenario].InterceptedMethod;
        var compilation = Run(scenario).Output;
        var tree = compilation.SyntaxTrees.Single(static tree => tree.FilePath == "Consumer.cs");
        var invocation = tree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == method);
        var model = compilation.GetSemanticModel(tree);
        return (model.GetSymbolInfo(invocation).Symbol as IMethodSymbol, model.GetInterceptorMethod(invocation));
    }

    private static GeneratorRun Run(string scenario) =>
        Runs.GetOrAdd(scenario, static name => new Lazy<GeneratorRun>(() => Generate(Scenarios[name]))).Value;

    private static GeneratorRun Generate(Scenario scenario)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(scenario.Source, ParseOptions, "Consumer.cs") };
        if (scenario.CommandSource)
            trees.Add(CSharpSyntaxTree.ParseText("using System; using System.Threading; using System.Threading.Tasks;\n" +
                File.ReadAllText(Repo.Root / "tests/Qyl.Telemetry.AutoInstrumentation.SourceGenerators.Snapshots/Fixture/SnapshotCommand.cs"),
                ParseOptions, "SnapshotCommand.cs"));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Append(typeof(Confluent.Kafka.IProducer<,>).Assembly.Location)
            .Append(typeof(System.ServiceModel.ClientBase<>).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("GeneratorConsumer", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        return new GeneratorRun(compilation, (CSharpCompilation)updated, driver.GetRunResult().Results);
    }

    private static string KafkaConsumer(bool asynchronous)
    {
        var method = asynchronous ? "ProduceAsync" : "Produce";
        var returnType = asynchronous ? "Task<DeliveryResult<string?, string?>>" : "void";
        return $$"""
using Confluent.Kafka;
using System.Threading.Tasks;

internal static class Consumer
{
    internal static {{returnType}} Send(
        IProducer<string?, string?> producer, Message<string?, string?> message)
        => producer.{{method}}("topic", message);
}
""";
    }

    private static string ObsoleteDbConsumer(bool asynchronous)
    {
        var returnType = asynchronous ? "System.Threading.Tasks.Task<object?>" : "object?";
        var method = asynchronous ? "ExecuteScalarAsync" : "ExecuteScalar";
        var body = asynchronous ? "System.Threading.Tasks.Task.FromResult<object?>(null)" : "null";
        return $$"""
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
""";
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
}
