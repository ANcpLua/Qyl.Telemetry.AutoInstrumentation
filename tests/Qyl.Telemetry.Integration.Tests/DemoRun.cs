using System.Text.Json;
using Qyl.Telemetry.Tests;

namespace Qyl.Telemetry.Integration.Tests;

enum LaneRuntime
{
    Managed,
    NativeAot,
}

/// <summary>A run's complete OTLP capture, its registry report (absent when nothing was exported) and the file holding both.</summary>
sealed record DemoCapture(TelemetrySnapshot Snapshot, RegistryReport? Registry, string Path);

/// <summary>
/// One execution of a demo: its process result, its independent report and its complete OTLP capture.
/// It only reads; the lane tests assert. A field the report lacks, or carries in another shape, throws
/// <see cref="InvalidDataException" /> with the whole report, so a missing field can never read as an
/// empty or zero value.
/// </summary>
sealed class DemoRun(string laneName, string key, LaneRuntime runtime, ProcessResult process, DemoCapture capture)
{
    private JsonElement? _report;

    public string Key { get; } = key;

    public string Label { get; } = $"{laneName} {key}";

    public ProcessResult Process { get; } = process;

    public DemoCapture Capture { get; } = capture;

    public TelemetrySnapshot Telemetry => Capture.Snapshot;

    public string Stdout => Process.Stdout;

    public string ExpectedRuntimeMode => runtime is LaneRuntime.NativeAot ? "nativeaot" : "dynamic-code-supported";

    /// <summary>The JSON object the demo prints last, from its first <c>{\n</c> to the end of stdout.</summary>
    public JsonElement Report
    {
        get
        {
            if (_report is { } cached)
                return cached;
            var start = Process.Stdout.IndexOf("{\n", StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidDataException($"{Label} did not emit a JSON report\n{Process.Describe()}");
            using var document = JsonDocument.Parse(Process.Stdout[start..]);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                throw new InvalidDataException($"{Label} report is not a JSON object\n{Process.Describe()}");
            _report = document.RootElement.Clone();
            return _report.Value;
        }
    }

    public string Dump() => JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true });

    public JsonElement Field(string field) =>
        Report.TryGetProperty(field, out var value) ? value : throw new InvalidDataException($"{Label} report has no {field}:\n{Dump()}");

    public string? StringField(string field) =>
        Report.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    public bool? BoolField(string field) =>
        Report.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    public int? IntField(string field) =>
        Report.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    public JsonElement[] Array(string field)
    {
        var value = Field(field);
        return value.ValueKind is JsonValueKind.Array
            ? [.. value.EnumerateArray()]
            : throw new InvalidDataException($"{Label} report {field} is not an array: {value}");
    }

    public IReadOnlyList<string?> Strings(string field) => Strings(Array(field));

    public int TokenCount(string token)
    {
        var count = 0;
        for (var index = Stdout.IndexOf(token, StringComparison.Ordinal); index >= 0; index = Stdout.IndexOf(token, index + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    public static IReadOnlyList<string?> Strings(IEnumerable<JsonElement> items) =>
        [.. items.Select(static item => item.ValueKind is JsonValueKind.String ? item.GetString() : null)];
}

/// <summary>Reads a reported activity's fields whichever of the demos' record shapes it has.</summary>
static class ReportedActivity
{
    public static string? Text(JsonElement activity, string field) =>
        activity.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    public static bool? Bool(JsonElement activity, string field) =>
        activity.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>A tag's text: a plain string, or the <c>Value</c> of a typed tag record.</summary>
    public static string? Tag(JsonElement activity, string key)
    {
        if (!activity.TryGetProperty("Tags", out var tags) || tags.ValueKind is not JsonValueKind.Object || !tags.TryGetProperty(key, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("Value", out var inner) => inner.ValueKind is JsonValueKind.String ? inner.GetString() : inner.ToString(),
            JsonValueKind.Null => null,
            _ => value.ToString(),
        };
    }

    public static bool HasTag(JsonElement activity, string key) =>
        activity.TryGetProperty("Tags", out var tags) && tags.ValueKind is JsonValueKind.Object && tags.TryGetProperty(key, out _);

    public static JsonElement TagValue(JsonElement activity, string key)
    {
        if (!activity.TryGetProperty("Tags", out var tags) || !tags.TryGetProperty(key, out var value))
            return default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("Value", out var inner) ? inner : value;
    }

    public static IEnumerable<string> TagKeys(JsonElement activity) =>
        activity.TryGetProperty("Tags", out var tags) && tags.ValueKind is JsonValueKind.Object
            ? tags.EnumerateObject().Select(static property => property.Name)
            : [];

    public static IEnumerable<JsonElement> Named(IEnumerable<JsonElement> activities, string source, string name) =>
        activities.Where(activity => Text(activity, "Source") == source && Text(activity, "Name") == name);
}
