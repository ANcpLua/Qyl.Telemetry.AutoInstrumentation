using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class WcfClientInterceptionTests(TestContext testContext)
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    [TestMethod]
    public void WcfClient_NonContractMethod_IsNotIntercepted()
    {
        var tree = CSharpSyntaxTree.ParseText("""
using System.ServiceModel;

[ServiceContract]
internal interface IOrderService
{
    [OperationContract]
    string GetOrder(int id);
}

internal sealed class OrdersClient : ClientBase<IOrderService>, IOrderService
{
    public string GetOrder(int id) => Channel.GetOrder(id);

    public string Describe(int id) => "order #" + id;

    public override string ToString() => "OrdersClient";
}

internal static class Consumer
{
    internal static string Run(OrdersClient client) => client.GetOrder(1) + client.Describe(1) + client.ToString();
}
""", ParseOptions, "Consumer.cs", cancellationToken: testContext.CancellationToken);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Append(typeof(System.ServiceModel.ClientBase<>).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("WcfConsumer", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, testContext.CancellationToken);

        var model = updated.GetSemanticModel(tree);
        var intercepted = tree.GetRoot(testContext.CancellationToken).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "client" } })
            .ToDictionary(
                static invocation => ((MemberAccessExpressionSyntax)invocation.Expression).Name.Identifier.ValueText,
                invocation => model.GetInterceptorMethod(invocation, testContext.CancellationToken) is not null);

        Assert.IsTrue(intercepted["GetOrder"], "The contract operation is the RPC and must be intercepted.");
        Assert.IsFalse(intercepted["Describe"], "A helper that is not a contract operation was given an RPC client span.");
        Assert.IsFalse(intercepted["ToString"], "ToString was given an RPC client span.");
    }
}
