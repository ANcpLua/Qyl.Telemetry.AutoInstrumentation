using System.Diagnostics;
using System.Reflection;
using Confluent.Kafka;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class KafkaPartitionTests(TestContext testContext)
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    [TestMethod]
    public void KafkaProduce_UnassignedPartition_ReportsNoPartitionId()
    {
        var tree = CSharpSyntaxTree.ParseText("""
using Confluent.Kafka;

internal static class Consumer
{
    internal static void Send(IProducer<string, string> producer)
    {
        producer.Produce(new TopicPartition("orders", Partition.Any), new Message<string, string> { Key = "k", Value = "v" });
        producer.Produce(new TopicPartition("orders", new Partition(3)), new Message<string, string> { Key = "k", Value = "v" });
    }
}
""", ParseOptions, "Consumer.cs", cancellationToken: testContext.CancellationToken);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Append(typeof(IProducer<,>).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("KafkaPartitionConsumer", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, testContext.CancellationToken);
        using var image = new MemoryStream();
        var emitted = updated.Emit(image, cancellationToken: testContext.CancellationToken);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var send = Assembly.Load(image.ToArray()).GetType("Consumer", throwOnError: true)!
            .GetMethod("Send", BindingFlags.Static | BindingFlags.NonPublic)!;

        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == QylActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);
        send.Invoke(null, [DispatchProxy.Create<IProducer<string, string>, AcceptingProducer>()]);

        Assert.HasCount(2, started);
        Assert.AreEqual("3", started[1].GetTagItem("messaging.destination.partition.id"));
        Assert.IsNull(started[0].GetTagItem("messaging.destination.partition.id"),
            "Partition.Any was reported as partition id " + started[0].GetTagItem("messaging.destination.partition.id"));
    }

    /// <summary>An <see cref="IProducer{TKey,TValue}"/> that accepts every call.</summary>
    internal class AcceptingProducer : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
