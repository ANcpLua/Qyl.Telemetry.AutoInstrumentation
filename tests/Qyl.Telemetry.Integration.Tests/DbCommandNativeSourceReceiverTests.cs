using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class DbCommandNativeSourceReceiverTests(TestContext testContext)
{
    private const string NativeSourceNamespace = "Npgsql";
    private const string NativeSourceChildNamespace = "Npgsql.Internal";
    private const string NativeSourcePrefixedNamespace = "NpgsqlCompat";

    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    [TestMethod]
    [DataRow(NativeSourceNamespace, false)]
    [DataRow(NativeSourceChildNamespace, false)]
    [DataRow(NativeSourcePrefixedNamespace, true)]
    public void DbCommandExecuteScalar_ReceiverNamespace_InterceptedOnlyOutsideNativeSourceNamespaces(string receiverNamespace, bool intercepted)
    {
        var tree = CSharpSyntaxTree.ParseText($$"""
using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace {{receiverNamespace}}
{
    public sealed class ProviderCommand : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = "SELECT 1";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}

internal static class Consumer
{
    internal static object? Run(global::{{receiverNamespace}}.ProviderCommand command) => command.ExecuteScalar();
}
""", ParseOptions, "Consumer.cs", cancellationToken: testContext.CancellationToken);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("NativeSourceReceiverConsumer", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, testContext.CancellationToken);

        var invocation = tree.GetRoot(testContext.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        Assert.AreEqual(intercepted, updated.GetSemanticModel(tree).GetInterceptorMethod(invocation, testContext.CancellationToken) is not null);
    }
}
