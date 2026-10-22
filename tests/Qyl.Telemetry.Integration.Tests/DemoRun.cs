using System.Text.Json;
using Qyl.Telemetry.Testing;

namespace Qyl.Telemetry.Integration.Tests;

enum LaneRuntime
{
    Managed,
    NativeAot,
}

/// <summary>
/// One execution of a demo: its process result, independent report and complete OTLP capture.
/// The assertions restate what the demo already asserts in-process, so a
/// demo that quietly relaxes its own expectation still fails its lane.
/// </summary>
sealed class DemoRun(string label, LaneRuntime runtime, ProcessResult process, TelemetrySnapshot telemetry)
{
    private JsonElement? _report;

    public string Label { get; } = label;

    private LaneRuntime Runtime { get; } = runtime;

    public ProcessResult Process { get; } = process;

    public TelemetrySnapshot Telemetry { get; } = telemetry;

    public string Stdout => Process.Stdout;

    public string ExpectedRuntimeMode => Runtime is LaneRuntime.NativeAot ? "nativeaot" : "dynamic-code-supported";

    /// <summary>Exit code 0 and nothing on stderr.</summary>
    public DemoRun RequireSucceeded()
    {
        Assert.AreEqual(0, Process.ExitCode, $"{Label} failed\nexit={Process.ExitCode}\nstdout={Process.Stdout}\nstderr={Process.Stderr}");
        Assert.AreEqual(0, Process.Stderr.Length, $"{Label} wrote stderr:\n{Process.Stderr}");
        return this;
    }

    public DemoRun RequireTokens(params string[] tokens)
    {
        foreach (var token in tokens)
            Assert.IsTrue(Process.Stdout.Contains(token, StringComparison.Ordinal), $"{Label} missing output token '{token}'\nstdout={Process.Stdout}");
        return this;
    }

    public DemoRun RequireTokenCount(string token, int count)
    {
        var actual = Count(Process.Stdout, token);
        Assert.AreEqual(count, actual, $"{Label} expected exactly {count} '{token}' tokens, found {actual}\nstdout={Process.Stdout}");
        return this;
    }

    /// <summary>The JSON report the demo prints last, from its first <c>{\n</c> to the end.</summary>
    private JsonElement Report
    {
        get
        {
            if (_report is { } cached)
                return cached;
            var start = Process.Stdout.IndexOf("{\n", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, start, $"{Label} did not emit a JSON report\nstdout={Process.Stdout}");
            using var document = ParseJson(Process.Stdout[start..], out var parseError);
            Assert.IsNotNull(document, $"{Label} emitted an invalid JSON report: {parseError}\nstdout={Process.Stdout}");
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind, $"{Label} report must be a JSON object");
            _report = document.RootElement.Clone();
            return _report.Value;
        }
    }

    private static JsonDocument? ParseJson(string json, out string? error)
    {
        try
        {
            error = null;
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    public string Dump() => JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>RuntimeMode matches the runtime this run used, and the demo's own verdict is Pass.</summary>
    public DemoRun RequireReportPassed()
    {
        Assert.AreEqual(ExpectedRuntimeMode, StringField("RuntimeMode"), $"{Label} runtime mode mismatch: expected={ExpectedRuntimeMode} actual={StringField("RuntimeMode")}");
        return RequirePass();
    }

    public DemoRun RequirePass()
    {
        Assert.IsTrue(Has("Pass") && Report.GetProperty("Pass").ValueKind is JsonValueKind.True, $"{Label} report did not pass:\n{Dump()}");
        return this;
    }

    private bool Has(string field) => Report.TryGetProperty(field, out _);

    public JsonElement Field(string field)
    {
        Assert.IsTrue(Report.TryGetProperty(field, out var value), $"{Label} report has no {field}:\n{Dump()}");
        return value;
    }

    public string? StringField(string field) =>
        Report.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    public bool? BoolField(string field) =>
        Report.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    public DemoRun RequireBool(string field, bool expected)
    {
        Assert.AreEqual(expected, BoolField(field), $"{Label} expected {field}={expected}, got {BoolField(field)?.ToString() ?? "absent"}:\n{Dump()}");
        return this;
    }

    public JsonElement Array(string field)
    {
        var value = Field(field);
        Assert.AreEqual(JsonValueKind.Array, value.ValueKind, $"{Label} report {field} must be an array: {value}");
        return value;
    }

    public JsonElement RequireCount(string field, int expected, string what)
    {
        var array = Array(field);
        Assert.AreEqual(expected, array.GetArrayLength(), $"{Label} expected exactly {expected} {what}, got {array.GetArrayLength()}:\n{array}");
        return array;
    }

    public JsonElement RequireAtLeast(string field, int expected, string what)
    {
        var array = Array(field);
        Assert.IsGreaterThanOrEqualTo(expected, array.GetArrayLength(), $"{Label} expected at least {expected} {what}, got {array.GetArrayLength()}:\n{array}");
        return array;
    }

    public static IReadOnlyList<string?> Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(static item => item.ValueKind is JsonValueKind.String ? item.GetString() : null)];

    private static int Count(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0; index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

/// <summary>Reads a reported activity's fields whichever of the demos' record shapes it has.</summary>
static class ReportedActivity
{
    public static string? Text(JsonElement activity, string field) =>
        activity.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

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
}
