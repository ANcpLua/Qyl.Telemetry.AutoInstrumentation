using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using GenAiAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.GenAi.GenAiAttributes;
using MessagingAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Messaging.MessagingAttributes;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class NativeSpanRepresentationTests
{
    private const string MeaiSource = "Experimental.Microsoft.Extensions.AI";
    private const string AgentsSource = "Experimental.Microsoft.Agents.AI";
    private const string MassTransitSource = "MassTransit";
    private const string OtherProducerSource = "qyl.tests.other-producer";

    [TestMethod]
    [DataRow(MeaiSource)]
    [DataRow(AgentsSource)]
    public void GenAiFinishReasons_JsonStringArray_ExportAsAStringArray(string source)
    {
        using var pipeline = new RepresentationPipeline(source);

        var exported = pipeline.Export(GenAiAttributes.ResponseFinishReasons, "[\"stop\",\"\",\"line\\nbreak\",\"quoted\\\"value\"]");

        Assert.AreSequenceEqual(["stop", "", "line\nbreak", "quoted\"value"], Assert.IsInstanceOfType<string[]>(exported));
    }

    [TestMethod]
    [DataRow(MeaiSource)]
    [DataRow(AgentsSource)]
    public void GenAiFinishReasons_EmptyJsonArray_ExportAsAnEmptyArray(string source)
    {
        using var pipeline = new RepresentationPipeline(source);

        var exported = pipeline.Export(GenAiAttributes.ResponseFinishReasons, "[]");

        Assert.IsEmpty(Assert.IsInstanceOfType<string[]>(exported));
    }

    [TestMethod]
    [DataRow(MeaiSource, "[\"stop\",1]")]
    [DataRow(MeaiSource, "[null]")]
    [DataRow(MeaiSource, "{}")]
    [DataRow(MeaiSource, "null")]
    [DataRow(MeaiSource, "\"stop\"")]
    [DataRow(MeaiSource, "{")]
    [DataRow(AgentsSource, "[\"stop\",1]")]
    [DataRow(AgentsSource, "[null]")]
    [DataRow(AgentsSource, "{}")]
    [DataRow(AgentsSource, "null")]
    [DataRow(AgentsSource, "\"stop\"")]
    [DataRow(AgentsSource, "{")]
    public void GenAiFinishReasons_NotAStringArray_ExportUnchanged(string source, string original)
    {
        using var pipeline = new RepresentationPipeline(source);

        Assert.AreEqual(original, pipeline.Export(GenAiAttributes.ResponseFinishReasons, original));
    }

    [TestMethod]
    [DataRow("0", 0L)]
    [DataRow("9223372036854775807", long.MaxValue)]
    public void MassTransitBodySize_NonnegativeInt64Text_ExportsTheExactInt64(string text, long size)
    {
        using var pipeline = new RepresentationPipeline(MassTransitSource);

        Assert.AreEqual(size, Assert.IsInstanceOfType<long>(pipeline.Export(MessagingAttributes.MessageBodySize, text)));
    }

    [TestMethod]
    [DataRow("9223372036854775808")]
    [DataRow("-1")]
    [DataRow("+1")]
    [DataRow("1.0")]
    [DataRow(" 1")]
    [DataRow("1 ")]
    [DataRow("")]
    [DataRow("bytes")]
    public void MassTransitBodySize_UnconvertibleText_ExportsUnchanged(string original)
    {
        using var pipeline = new RepresentationPipeline(MassTransitSource);

        Assert.AreEqual(original, pipeline.Export(MessagingAttributes.MessageBodySize, original));
    }

    [TestMethod]
    public void OtherProducer_FinishReasonsJson_ExportsUnchanged()
    {
        using var pipeline = new RepresentationPipeline(OtherProducerSource);

        Assert.AreEqual("[\"stop\"]", pipeline.Export(GenAiAttributes.ResponseFinishReasons, "[\"stop\"]"));
    }

    [TestMethod]
    public void OtherProducer_NumericBodySize_ExportsUnchanged()
    {
        using var pipeline = new RepresentationPipeline(OtherProducerSource);

        Assert.AreEqual("42", pipeline.Export(MessagingAttributes.MessageBodySize, "42"));
    }

    [TestMethod]
    public void MassTransit_UnrelatedNumericAttribute_ExportsUnchanged()
    {
        using var pipeline = new RepresentationPipeline(MassTransitSource);

        Assert.AreEqual("42", pipeline.Export("checkout.text", "42"));
    }

    /// <summary>
    /// A built AddQyl host with an in-memory exporter, subscribed to one source and with no OTLP endpoint
    /// in the environment; <see cref="Export" /> returns the value one exported span carries.
    /// </summary>
    private sealed class RepresentationPipeline : IDisposable
    {
        private readonly EnvironmentScope _environment = new(("OTEL_EXPORTER_OTLP_ENDPOINT", null));
        private readonly List<Activity> _exported = [];
        private readonly IHost _host;
        private readonly ActivitySource _source;

        public RepresentationPipeline(string sourceName)
        {
            var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.AddQyl(options =>
            {
                options.RequireConfiguredEndpoint = true;
                options.EnableCollectorDiscovery = false;
                options.EnableLogExport = false;
                options.EnableMetricsExport = false;
                options.AdditionalSources.Add(sourceName);
                options.ConfigureTracing = tracing => tracing.AddInMemoryExporter(_exported);
            });
            _host = builder.Build();
            _ = _host.Services.GetRequiredService<TracerProvider>();
            _source = new ActivitySource(sourceName);
        }

        public object? Export(string key, string value)
        {
            _exported.Clear();
            using (var activity = _source.CreateActivity("wire-representation", ActivityKind.Internal)?.Start()
                                  ?? throw new InvalidOperationException($"AddQyl did not subscribe to {_source.Name}."))
            {
                activity.SetTag(key, value);
            }

            return _exported.Single().GetTagItem(key);
        }

        public void Dispose()
        {
            _source.Dispose();
            _host.Dispose();
            _environment.Dispose();
        }
    }
}
