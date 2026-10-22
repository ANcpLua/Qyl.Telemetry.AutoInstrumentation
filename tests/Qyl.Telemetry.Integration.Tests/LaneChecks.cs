using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>Report shapes several demos share, each restated so a demo cannot relax its own expectation.</summary>
static class LaneChecks
{
    /// <summary>
    /// The exact sorted multiset of spans per operation, pinned here as well as in the demo. Each entry of
    /// <paramref name="field"/> names its operation under <paramref name="key"/> and carries the demo's
    /// own Expected and its Actual list.
    /// </summary>
    public static void SpanSets(DemoRun run, string field, string key, IReadOnlyDictionary<string, string[]> expected, bool sortExpected = false)
    {
        var entries = run.Array(field).EnumerateArray().ToArray();
        var observed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            Assert.AreEqual(JsonValueKind.Object, entry.ValueKind, $"{run.Label}: {field} entry is not an object: {entry}");
            observed[ReportedActivity.Text(entry, key) ?? string.Empty] = entry;
        }

        Assert.IsTrue(observed.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected.Keys.Order(StringComparer.Ordinal)), $"{run.Label}: {field} operations are [{string.Join(", ", observed.Keys.Order(StringComparer.Ordinal))}], expected [{string.Join(", ", expected.Keys.Order(StringComparer.Ordinal))}]");
        Assert.HasCount(expected.Count, entries, $"{run.Label}: expected {expected.Count} {field} entries, got {entries.Length}");
        foreach (var (operation, spans) in expected)
        {
            var want = sortExpected ? [.. spans.Order(StringComparer.Ordinal)] : spans;
            foreach (var side in new[] { "Expected", "Actual" })
            {
                var actual = observed[operation].TryGetProperty(side, out var list) && list.ValueKind is JsonValueKind.Array ? DemoRun.Strings(list) : null;
                Assert.IsTrue(actual is not null && actual.SequenceEqual(want), $"{run.Label}: {field} {operation} {side} was [{string.Join(", ", actual ?? [])}], expected [{string.Join(", ", want)}]\n{run.Dump()}");
            }
        }
    }

    /// <summary>The demo's raw listener drops the runtime's own Experimental.* diagnostics and nothing else.</summary>
    public static void ExcludedWithPrefix(DemoRun run, string field, string prefix, bool requireSome)
    {
        var excluded = run.Array(field);
        Assert.IsTrue(!requireSome || excluded.GetArrayLength() > 0, $"{run.Label}: expected the demo to have seen and excluded {prefix}* sources, got none");
        foreach (var scope in excluded.EnumerateArray())
        {
            Assert.IsTrue(scope.ValueKind is JsonValueKind.String && scope.GetString()!.StartsWith(prefix, StringComparison.Ordinal),
                $"{run.Label}: excluded a scope outside the {prefix}* rule: {scope}");
        }
    }

    public static void ExactStrings(DemoRun run, string field, IReadOnlyList<string> expected)
    {
        var actual = DemoRun.Strings(run.Array(field));
        Assert.IsTrue(actual.SequenceEqual(expected), $"{run.Label}: {field} is [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]");
    }

    /// <summary>A report's metric records as measurement series: the instrument under <paramref name="nameField"/>
    /// and the record's Tags, when it has any.</summary>
    public static IReadOnlyList<ReportedMetric> Metrics(JsonElement metrics, string nameField = "Name") =>
        [.. metrics.EnumerateArray()
            .Select(metric => new ReportedMetric(
                ReportedActivity.Text(metric, nameField) ?? throw new AssertFailedException($"a reported metric carries no {nameField}: {metric}"),
                ReportedActivity.TagKeys(metric).ToDictionary(static key => key, key => ReportedActivity.Tag(metric, key) ?? string.Empty, StringComparer.Ordinal),
                ReportedActivity.Text(metric, "MeterName")))];

    /// <summary>A synthetic span list for reports whose records are not activities but name one by id.</summary>
    public static JsonElement SpanReferences(IEnumerable<(string TraceId, string SpanId, IReadOnlyDictionary<string, string> Tags)> spans) =>
        JsonSerializer.SerializeToElement(spans.Select(static span => new Dictionary<string, object>
        {
            ["TraceId"] = span.TraceId,
            ["SpanId"] = span.SpanId,
            ["Tags"] = span.Tags,
        }).ToArray());
}
