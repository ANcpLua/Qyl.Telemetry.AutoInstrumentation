using System.Globalization;
using System.Text.Json;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.Tests;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>
/// Compares an application's independent report with its complete OTLP capture. Every method returns the
/// differences it found, one line each; the lane tests assert that a list is empty or equal to the expected one.
/// </summary>
static class CapturedTelemetry
{
    /// <summary>Each reported span whose identity the capture does not hold exactly once.</summary>
    public static IReadOnlyList<string> SpanExportCounts(DemoRun run, IEnumerable<JsonElement> reported) =>
        [.. reported.Select(expected => (Id: SpanIdentity(expected), Count: Exported(run, expected).Length))
            .Where(static span => span.Count != 1)
            .Select(static span => $"span {span.Id} was exported {span.Count} times")];

    /// <summary>Each field of a reported span, exported exactly once, that the exported span does not carry unchanged.</summary>
    public static IReadOnlyList<string> SpanDifferences(DemoRun run, IEnumerable<JsonElement> reported) =>
        [.. reported.SelectMany(expected => Exported(run, expected) is [var span] ? Compare(expected, span) : [])];

    public static IReadOnlyList<string> ServiceSpanIds(DemoRun run, string serviceName) =>
        [.. run.Telemetry.Spans.Where(span => span.Service == serviceName).Select(static span => span.SpanId)];

    /// <summary>Each reported measurement series for which no exported point of the service carries all reported attributes.</summary>
    public static IReadOnlyList<string> UnmatchedMetrics(DemoRun run, string serviceName, IEnumerable<ReportedMetric> metrics) =>
        [.. metrics.Where(expected => !run.Telemetry.Metrics
                .Where(metric => metric.Name == expected.Name &&
                                 (expected.ScopeName is null || metric.Scope?.Name == expected.ScopeName) &&
                                 metric.Resource.Attributes.TryGetValue("service.name", out var service) && Equals(service, serviceName))
                .SelectMany(static metric => Points(metric.Data))
                .Any(attributes => expected.Attributes.All(pair => attributes.TryGetValue(pair.Key, out var value) && Text(value) == pair.Value)))
            .Select(static expected => $"no exported {expected.Name} point carries all reported attributes: {string.Join(", ", expected.Attributes)}")];

    public static IReadOnlyList<string> MetricNames(DemoRun run) => [.. run.Telemetry.Metrics.Select(static metric => metric.Name)];

    public static IReadOnlyList<(string Severity, string? Body)> ServiceLogs(DemoRun run, string serviceName) =>
        [.. run.Telemetry.Logs
            .Where(record => record.Resource.Attributes.TryGetValue("service.name", out var service) && Equals(service, serviceName))
            .Select(static record => (Severity(record.SeverityNumber), Text(record.Body)))];

    private static SpanRecord[] Exported(DemoRun run, JsonElement expected)
    {
        var traceId = Id(expected, "TraceId");
        var spanId = Id(expected, "SpanId");
        return [.. run.Telemetry.Spans.Where(span => span.TraceId == traceId && span.SpanId == spanId)];
    }

    private static string SpanIdentity(JsonElement expected) => $"{Id(expected, "TraceId")}/{Id(expected, "SpanId")}";

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Points(MetricData data) => data switch
    {
        GaugeData gauge => gauge.Points.Select(static point => point.Attributes),
        SumData sum => sum.Points.Select(static point => point.Attributes),
        HistogramData histogram => histogram.Points.Select(static point => point.Attributes),
        ExponentialHistogramData histogram => histogram.Points.Select(static point => point.Attributes),
        SummaryData summary => summary.Points.Select(static point => point.Attributes),
        _ => throw new InvalidDataException("The exporter sent a metric without data."),
    };

    private static List<string> Compare(JsonElement expected, SpanRecord span)
    {
        var differences = new List<string>();
        var context = $"span {span.TraceId}/{span.SpanId}";

        void Differ<T>(T want, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(want, actual))
                differences.Add($"{context} {what}: reported {want}, exported {actual}");
        }

        if (ReportedActivity.Text(expected, "Name") is { } name)
            Differ(name, span.Name, "name");
        if (ReportedActivity.Text(expected, "Source") is { } source)
            Differ(source, span.Scope?.Name, "scope");
        if (ReportedActivity.Text(expected, "Kind") is { } kind)
            Differ(kind, span.Kind.ToString(), "kind");
        if (ReportedActivity.Text(expected, "ParentSpanId") is { } parent)
            Differ(parent == "0000000000000000" ? null : parent, span.ParentSpanId, "parent");
        if (ReportedActivity.Text(expected, "Status") is { } status)
        {
            var code = status == "Unset" ? ReportedActivity.Tag(expected, "otel.status_code") switch
            {
                "OK" => "Ok", "ERROR" => "Error", _ => status,
            } : status;
            Differ(code, span.Status.ToString(), "status");
        }

        foreach (var key in ReportedActivity.TagKeys(expected))
        {
            // The OTLP exporter consumes these Activity control tags into Status, rather than
            // exporting them as attributes. Compare their resulting status fields instead.
            if (key == "otel.status_code")
            {
                Differ(ReportedActivity.Tag(expected, key) == "ERROR" ? "Error" : "Ok", span.Status.ToString(), "status tag");
                continue;
            }
            if (key == "otel.status_description")
            {
                Differ(ReportedActivity.Tag(expected, key), span.StatusDescription, "status description");
                continue;
            }

            var value = ReportedActivity.Tag(expected, key);
            if (value is null)
            {
                if (span.Attributes.ContainsKey(key))
                    differences.Add($"{context}: a null Activity tag became an OTLP attribute: {key}");
                continue;
            }
            if (value.StartsWith("System.", StringComparison.Ordinal) && value.EndsWith("[]", StringComparison.Ordinal))
            {
                differences.Add($"{context}: the demo report lost the array value of {key}; preserve it in the report");
                continue;
            }
            if (!span.Attributes.TryGetValue(key, out var actual))
            {
                differences.Add($"{context}: missing attribute {key}");
                continue;
            }

            var reported = ReportedActivity.TagValue(expected, key);
            if (reported.ValueKind != JsonValueKind.Array)
            {
                Differ(value, Text(actual), "attribute " + key);
                continue;
            }
            if (actual is not IReadOnlyList<object?> items || !items.All(static item => item is string))
            {
                differences.Add($"{context}: attribute {key} is not a string array: {actual}");
                continue;
            }

            var expectedItems = reported.EnumerateArray().Select(static item => item.GetString()).ToArray();
            if (!expectedItems.SequenceEqual(items.Cast<string>()))
                differences.Add($"{context} array values {key}: reported [{string.Join(", ", expectedItems)}], exported [{string.Join(", ", items)}]");
        }

        return differences;
    }

    private static string Id(JsonElement activity, string field) =>
        ReportedActivity.Text(activity, field) ?? throw new InvalidDataException($"A reported span carries no {field}: {activity}");

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
