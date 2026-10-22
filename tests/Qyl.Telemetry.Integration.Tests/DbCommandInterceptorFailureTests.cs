using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class DbCommandInterceptorFailureTests(TestContext testContext)
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    [TestMethod]
    public async Task AsyncDbCommand_ThrowsBeforeReturningTask_EndsSpan()
    {
        var compilation = Generate("""
using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

internal sealed class InlineFailingCommand : DbCommand
{
    [AllowNull]
    public override string CommandText { get; set; } = "SELECT * FROM missing_table";
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

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("SQLite Error 1: 'no such table: missing_table'.");
}

internal static class Consumer
{
    internal static async Task<Activity?> Run(InlineFailingCommand command)
    {
        try
        {
            await command.ExecuteScalarAsync();
        }
        catch (InvalidOperationException)
        {
        }

        return Activity.Current;
    }
}
""");
        var assembly = Load(compilation);
        var started = new List<Activity>();
        using var listener = ListenToQylSpans(started);
        using var command = (IDisposable)Activator.CreateInstance(assembly.GetType("InlineFailingCommand", throwOnError: true)!)!;
        var run = assembly.GetType("Consumer", throwOnError: true)!.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;

        var currentAfterFailure = await (Task<Activity?>)run.Invoke(null, [command])!;

        var span = Assert.ContainsSingle(started, "The ExecuteScalarAsync call site must be intercepted.");
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.IsTrue(span.IsStopped, "The failed command's span was never stopped, so it is never exported.");
        Assert.IsNull(currentAfterFailure,
            $"Activity.Current still points at the failed command's span '{currentAfterFailure?.DisplayName}' after the caller handled the exception.");
    }

    private CSharpCompilation Generate(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("DbCommandFailureConsumer",
            [CSharpSyntaxTree.ParseText(source, ParseOptions, "Consumer.cs", cancellationToken: testContext.CancellationToken)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, testContext.CancellationToken);
        return (CSharpCompilation)updated;
    }

    private static Assembly Load(Compilation compilation)
    {
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(image.ToArray());
    }

    private static ActivityListener ListenToQylSpans(List<Activity> started)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == QylActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
