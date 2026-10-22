using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class GenAiOptInDiagnosticTests(TestContext testContext)
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    [TestMethod]
    public void ChatClient_WrappedInOpenTelemetryChatClient_IsOptedIn()
    {
        var tree = CSharpSyntaxTree.ParseText("""
using Microsoft.Extensions.AI;

internal static class OptedInChat
{
    internal static object Chat(IChatClient inner) => new OpenTelemetryChatClient(inner).GetResponseAsync("probe");
}
""", ParseOptions, "Consumer.cs", cancellationToken: testContext.CancellationToken);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("OptInConsumer", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.IsEmpty(compilation.GetDiagnostics(testContext.CancellationToken).Where(static d => d.Severity is DiagnosticSeverity.Error));

        var driver = CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGenerators(compilation, testContext.CancellationToken);

        var reported = driver.GetRunResult().Results.Single().Diagnostics.Where(static d => d.Id == "QYL1002").ToArray();
        Assert.IsEmpty(reported, string.Join(Environment.NewLine, reported.Select(static d => d.GetMessage())));
    }
}
