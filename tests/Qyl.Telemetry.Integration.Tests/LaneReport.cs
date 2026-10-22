using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>Report shapes several demos share, read into plain values the lane tests compare with their own pins.</summary>
static class LaneReport
{
    public const string Expected = "Expected";
    public const string Actual = "Actual";

    /// <summary>
    /// One line per entry of <paramref name="field" />: its operation under <paramref name="key" /> and the
    /// span list on <paramref name="side" /> (the demo's own <see cref="Expected" /> or its <see cref="Actual" />).
    /// </summary>
    public static IReadOnlyList<string> SpanSets(DemoRun run, string field, string key, string side) =>
        [.. run.Array(field).Select(entry =>
        {
            if (entry.ValueKind is not JsonValueKind.Object)
                throw new InvalidDataException($"{run.Label}: {field} entry is not an object: {entry}");
            var spans = entry.TryGetProperty(side, out var list) && list.ValueKind is JsonValueKind.Array
                ? string.Join(", ", DemoRun.Strings(list.EnumerateArray()))
                : "absent";
            return SpanSetLine(ReportedActivity.Text(entry, key) ?? string.Empty, spans);
        })];

    /// <summary>The lines <see cref="SpanSets" /> yields when every operation carries exactly the pinned spans.</summary>
    public static IReadOnlyList<string> SpanSets(IReadOnlyDictionary<string, string[]> pinned, bool sort = false) =>
        [.. pinned.Select(pair => SpanSetLine(pair.Key, string.Join(", ", sort ? pair.Value.Order(StringComparer.Ordinal) : pair.Value)))];

    /// <summary>A report's metric records as measurement series: the instrument under <paramref name="nameField" />
    /// and the record's Tags, when it has any.</summary>
    public static IReadOnlyList<ReportedMetric> Metrics(IEnumerable<JsonElement> metrics, string nameField = "Name") =>
        [.. metrics.Select(metric => new ReportedMetric(
            ReportedActivity.Text(metric, nameField) ?? throw new InvalidDataException($"a reported metric carries no {nameField}: {metric}"),
            ReportedActivity.TagKeys(metric).ToDictionary(static key => key, key => ReportedActivity.Tag(metric, key) ?? string.Empty, StringComparer.Ordinal),
            ReportedActivity.Text(metric, "MeterName")))];

    /// <summary>A synthetic span list for reports whose records are not activities but name one by id.</summary>
    public static IReadOnlyList<JsonElement> SpanReferences(IEnumerable<(string TraceId, string SpanId, IReadOnlyDictionary<string, string> Tags)> spans) =>
        [.. spans.Select(static span => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["TraceId"] = span.TraceId,
            ["SpanId"] = span.SpanId,
            ["Tags"] = span.Tags,
        }))];

    public static IEnumerable<string> PrefixViolations(IEnumerable<string?> values, string prefix) =>
        values.Where(value => value is null || !value.StartsWith(prefix, StringComparison.Ordinal)).Select(static value => value ?? "<not a string>");

    private static string SpanSetLine(string operation, string spans) => $"{operation}: [{spans}]";
}
