using System.Globalization;
using System.Text.Json;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.Tests;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>Compares the application's independent report with its complete OTLP capture.</summary>
sealed class CapturedTelemetryCheck(string serviceName)
{
    public void Spans(DemoRun run, JsonElement activities, string what)
    {
        foreach (var expected in activities.EnumerateArray())
        {
            var traceId = Id(expected, "TraceId");
            var spanId = Id(expected, "SpanId");
            var span = Assert.ContainsSingle(run.Telemetry.Spans.Where(span => span.TraceId == traceId && span.SpanId == spanId),
                $"{run.Label}: {what} span {traceId}/{spanId} was not exported exactly once");
            Compare(run, expected, span);
        }
    }

    public void ServiceSpansExactly(DemoRun run, IReadOnlyCollection<string> spanIds, string what)
    {
        var actual = run.Telemetry.Spans.Where(span => span.Service == serviceName).Select(static span => span.SpanId).ToArray();
        Assert.AreSequenceEqual(spanIds, actual, SequenceOrder.InAnyOrder, $"{run.Label}: unexpected {what} span identities");
    }

    public void Metrics(DemoRun run, IEnumerable<ReportedMetric> metrics)
    {
        foreach (var expected in metrics)
        {
            var candidates = run.Telemetry.Metrics.Where(metric => metric.Name == expected.Name &&
                (expected.ScopeName is null || metric.Scope?.Name == expected.ScopeName) &&
                metric.Resource.Attributes.TryGetValue("service.name", out var service) && Equals(service, serviceName)).ToArray();
            Assert.Contains(attributes =>
                    expected.Attributes.All(pair => attributes.TryGetValue(pair.Key, out var value) &&
                                                    Text(value) == pair.Value), candidates.SelectMany(static metric => Points(metric.Data)),
                $"{run.Label}: no exported {expected.Name} point carries all reported attributes: {string.Join(", ", expected.Attributes)}");
        }
    }

    public void NoMetrics(DemoRun run, IEnumerable<string> metricNames)
    {
        var names = metricNames.ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(metric => names.Contains(metric.Name), run.Telemetry.Metrics,
            $"{run.Label}: a disabled metric was exported: {string.Join(", ", names)}");
    }

    public void Logs(DemoRun run, IReadOnlyList<(string Severity, string Body)> expected)
    {
        var records = run.Telemetry.Logs.Where(record => record.Resource.Attributes.TryGetValue("service.name", out var service) &&
            Equals(service, serviceName)).ToArray();
        Assert.HasCount(expected.Count, records, $"{run.Label}: unexpected exported log count");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Severity, Severity(records[i].SeverityNumber), $"{run.Label}: log {i} severity");
            Assert.AreEqual(expected[i].Body, Text(records[i].Body), $"{run.Label}: log {i} body");
        }
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Points(MetricData data) => data switch
    {
        GaugeData gauge => gauge.Points.Select(static point => point.Attributes),
        SumData sum => sum.Points.Select(static point => point.Attributes),
        HistogramData histogram => histogram.Points.Select(static point => point.Attributes),
        ExponentialHistogramData histogram => histogram.Points.Select(static point => point.Attributes),
        SummaryData summary => summary.Points.Select(static point => point.Attributes),
        _ => throw new AssertFailedException("The exporter sent a metric without data."),
    };

    private static void Compare(DemoRun run, JsonElement expected, SpanRecord span)
    {
        var context = $"{run.Label}: span {span.TraceId}/{span.SpanId}";
        if (ReportedActivity.Text(expected, "Name") is { } name)
            Assert.AreEqual(name, span.Name, context + " name");
        if (ReportedActivity.Text(expected, "Source") is { } source)
        {
            Assert.IsNotNull(span.Scope, context + " scope");
            Assert.AreEqual(source, span.Scope.Name, context + " scope");
        }
        if (ReportedActivity.Text(expected, "Kind") is { } kind)
            Assert.AreEqual(kind, span.Kind.ToString(), context + " kind");
        if (ReportedActivity.Text(expected, "ParentSpanId") is { } parent)
            Assert.AreEqual(parent == "0000000000000000" ? null : parent, span.ParentSpanId, context + " parent");
        if (ReportedActivity.Text(expected, "Status") is { } status)
        {
            var code = status == "Unset" ? ReportedActivity.Tag(expected, "otel.status_code") switch
            {
                "OK" => "Ok", "ERROR" => "Error", _ => status,
            } : status;
            Assert.AreEqual(code, span.Status.ToString(), context + " status");
        }

        foreach (var key in ReportedActivity.TagKeys(expected))
        {
            // The OTLP exporter consumes these Activity control tags into Status, rather than
            // exporting them as attributes. Compare their resulting status fields explicitly.
            if (key == "otel.status_code")
            {
                var code = ReportedActivity.Tag(expected, key) == "ERROR" ? "Error" : "Ok";
                Assert.AreEqual(code, span.Status.ToString(), context + " status tag");
                continue;
            }
            if (key == "otel.status_description")
            {
                Assert.AreEqual(ReportedActivity.Tag(expected, key), span.StatusDescription, context + " status description");
                continue;
            }

            var value = ReportedActivity.Tag(expected, key);
            if (value is null)
            {
                Assert.IsFalse(span.Attributes.ContainsKey(key), context + " null Activity tag must not become an OTLP attribute: " + key);
                continue;
            }
            Assert.IsFalse(value.StartsWith("System.", StringComparison.Ordinal) && value.EndsWith("[]", StringComparison.Ordinal),
                $"{context}: the demo report lost the array value of {key}; preserve it in the report");
            Assert.IsTrue(span.Attributes.TryGetValue(key, out var actual), context + " missing attribute " + key);
            var reported = ReportedActivity.TagValue(expected, key);
            if (reported.ValueKind == JsonValueKind.Array)
            {
                var items = Assert.IsInstanceOfType<IReadOnlyList<object?>>(actual, context + " array attribute " + key);
                var expectedItems = reported.EnumerateArray().Select(static item => item.GetString()).ToArray();
                Assert.IsTrue(items.All(static item => item is string), context + " string-array attribute " + key);
                Assert.AreSequenceEqual(expectedItems, items.Cast<string>(), context + " array values " + key);
            }
            else
                Assert.AreEqual(value, Text(actual), context + " attribute " + key);
        }
    }

    private static string Id(JsonElement activity, string field) =>
        ReportedActivity.Text(activity, field) ?? throw new AssertFailedException($"A reported span carries no {field}: {activity}");

    private static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string Severity(int number) => number switch
    {
        >= 21 => "Critical", >= 17 => "Error", >= 13 => "Warning", >= 9 => "Information",
        >= 5 => "Debug", >= 1 => "Trace", _ => "None",
    };
}

sealed record ReportedMetric(string Name, IReadOnlyDictionary<string, string> Attributes, string? ScopeName = null)
{
    public static ReportedMetric Named(string name) => new(name, new Dictionary<string, string>());
}
