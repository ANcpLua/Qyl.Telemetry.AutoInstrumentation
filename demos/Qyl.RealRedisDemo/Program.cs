using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qyl.Telemetry.AutoInstrumentation;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;

var configuration = Environment.GetEnvironmentVariable("QYL_REDIS_CONFIGURATION");
if (string.IsNullOrWhiteSpace(configuration))
{
    Console.Error.WriteLine("QYL_REDIS_CONFIGURATION is required.");
    return 2;
}

var captured = new List<CapturedActivity>();
var capturedLock = new Lock();
using var listener = new ActivityListener
{
    ShouldListenTo = static source => source.Name == "Qyl.Telemetry.AutoInstrumentation",
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity =>
    {
        lock (capturedLock)
        {
            captured.Add(CapturedActivity.From(activity));
        }
    },
};

ActivitySource.AddActivityListener(listener);

// The same spans over OTLP to the collector QYL_ENDPOINT names, which the Demos lane reads them back from.
using var tracerProvider = QylDemoOtlp.CreateTracerProvider("qyl-real-redis-demo", "Qyl.Telemetry.AutoInstrumentation");

var probes = new List<CommandProbe>();
await using (var connection = await WaitForRedisAsync(configuration))
{
    ProfilingSession? session = null;
    connection.RegisterProfiler(() => session);
    var database = connection.GetDatabase();

    // Each probe runs one intercepted call site, then pairs the span the generated interceptor
    // produced with the command StackExchange.Redis actually sent. The generator's command table
    // is only as good as this comparison — a wrong entry fails here rather than shipping a span
    // that names a command the server never saw.
    async Task<CommandProbe> Probe(string label, Func<IDatabaseAsync, Task> call)
    {
        lock (capturedLock)
        {
            captured.Clear();
        }

        session = new ProfilingSession();
        string? failure = null;
        try
        {
            await call(database);
        }
        catch (RedisServerException exception)
        {
            failure = exception.GetType().Name;
        }

        var wireCommands = session.FinishProfiling().Select(static command => command.Command).ToArray();
        session = null;

        CapturedActivity[] spans;
        lock (capturedLock)
        {
            spans = captured.ToArray();
            captured.Clear();
        }

        return new CommandProbe(label, wireCommands, spans, failure);
    }

    RedisKey str = "qyl:str", str2 = "qyl:str2", strFloat = "qyl:strfloat", hash = "qyl:hash", list = "qyl:list", set = "qyl:set", zset = "qyl:zset";
    await database.KeyDeleteAsync(new[] { str, str2, strFloat, hash, list, set, zset });
    await database.StringSetAsync(str, "10");
    await database.StringSetAsync(str2, "20");
    await database.StringSetAsync(strFloat, "1.5");
    await database.HashSetAsync(hash, "f", "v");
    await database.ListRightPushAsync(list, "a");
    await database.SetAddAsync(set, "m");
    await database.SortedSetAddAsync(zset, "m", 1);

    // Static single-command mappings.
    probes.Add(await Probe("StringSet", d => d.StringSetAsync(str, "10")));
    probes.Add(await Probe("StringGet", d => d.StringGetAsync(str)));
    probes.Add(await Probe("StringAppend", d => d.StringAppendAsync(str, "")));
    probes.Add(await Probe("KeyExists", d => d.KeyExistsAsync(str)));
    probes.Add(await Probe("KeyType", d => d.KeyTypeAsync(str)));
    probes.Add(await Probe("KeyTimeToLive", d => d.KeyTimeToLiveAsync(str)));
    probes.Add(await Probe("HashGetAll", d => d.HashGetAllAsync(hash)));
    probes.Add(await Probe("HashExists", d => d.HashExistsAsync(hash, "f")));
    probes.Add(await Probe("ListLength", d => d.ListLengthAsync(list)));
    probes.Add(await Probe("SetAdd", d => d.SetAddAsync(set, "m")));
    probes.Add(await Probe("SetLength", d => d.SetLengthAsync(set)));
    probes.Add(await Probe("SortedSetAdd", d => d.SortedSetAddAsync(zset, "m", 1)));
    probes.Add(await Probe("SortedSetScore", d => d.SortedSetScoreAsync(zset, "m")));

    // Overload discriminated by parameter type: the array overloads reach a different command.
    probes.Add(await Probe("StringGet.Multi", d => d.StringGetAsync(new[] { str, str2 })));
    probes.Add(await Probe("HashGet.Single", d => d.HashGetAsync(hash, "f")));
    probes.Add(await Probe("HashGet.Multi", d => d.HashGetAsync(hash, new RedisValue[] { "f", "g" })));
    probes.Add(await Probe("SetContains.Single", d => d.SetContainsAsync(set, "m")));
    probes.Add(await Probe("SetContains.Multi", d => d.SetContainsAsync(set, new RedisValue[] { "m", "n" })));
    probes.Add(await Probe("HashIncrement.Float", d => d.HashIncrementAsync(hash, "n", 1.5)));

    // The HashEntry[] overload returns a non-generic Task; it is instrumented like the rest.
    probes.Add(await Probe("HashSet.Entries", d => d.HashSetAsync(hash, new[] { new HashEntry("a", "1") })));

    // Overload discriminated by argument value at the call site.
    probes.Add(await Probe("StringIncrement.Unit", d => d.StringIncrementAsync(str)));
    probes.Add(await Probe("StringIncrement.By", d => d.StringIncrementAsync(str, 5)));
    probes.Add(await Probe("StringIncrement.Float", d => d.StringIncrementAsync(strFloat, 1.5)));
    probes.Add(await Probe("StringDecrement.Unit", d => d.StringDecrementAsync(str)));
    probes.Add(await Probe("StringDecrement.By", d => d.StringDecrementAsync(str, 5)));
    probes.Add(await Probe("StringIncrement.MinusOne", d => d.StringIncrementAsync(str, -1)));
    probes.Add(await Probe("StringIncrement.Negative", d => d.StringIncrementAsync(str, -5)));
    probes.Add(await Probe("StringDecrement.MinusOne", d => d.StringDecrementAsync(str, -1)));
    probes.Add(await Probe("StringDecrement.Negative", d => d.StringDecrementAsync(str, -5)));
    probes.Add(await Probe("SortedSetLength", d => d.SortedSetLengthAsync(zset)));
    probes.Add(await Probe("SortedSetLength.Bounded", d => d.SortedSetLengthAsync(zset, 1, 10)));
    probes.Add(await Probe("StringSet.NotExists", d => d.StringSetAsync(str, "10", default, ValueCondition.NotExists)));
    probes.Add(await Probe("StringSet.WhenNotExists", d => d.StringSetAsync(str, "10", TimeSpan.FromMinutes(5), When.NotExists)));
    probes.Add(await Probe("HashSet.Field", d => d.HashSetAsync(hash, "f", "v")));
    probes.Add(await Probe("HashSet.FieldNotExists", d => d.HashSetAsync(hash, "f", "v", When.NotExists)));
    probes.Add(await Probe("ListLeftPush", d => d.ListLeftPushAsync(list, "a")));
    probes.Add(await Probe("ListLeftPush.Exists", d => d.ListLeftPushAsync(list, "a", When.Exists)));
    probes.Add(await Probe("ListRightPush.Exists", d => d.ListRightPushAsync(list, "a", When.Exists)));
    // KeyExpireAsync reaches five commands from one method: a null or MaxValue expiry persists, and
    // StackExchange.Redis drops to second precision when the value carries no whole milliseconds.
    probes.Add(await Probe("KeyExpire.Seconds", d => d.KeyExpireAsync(str, TimeSpan.FromMinutes(5))));
    probes.Add(await Probe("KeyExpire.Milliseconds", d => d.KeyExpireAsync(str, TimeSpan.FromMilliseconds(1500))));
    probes.Add(await Probe("KeyExpire.SubMillisecondTicks", d => d.KeyExpireAsync(str, TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1))));
    probes.Add(await Probe("KeyExpire.Persist", d => d.KeyExpireAsync(str, (TimeSpan?)null)));
    probes.Add(await Probe("KeyExpire.At", d => d.KeyExpireAsync(str, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
    probes.Add(await Probe("KeyExpire.AtMilliseconds", d => d.KeyExpireAsync(str, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(500))));
    probes.Add(await Probe("KeyExpire.AtPersist", d => d.KeyExpireAsync(str, (DateTime?)null)));
    probes.Add(await Probe("KeyExpire.MaxValue", d => d.KeyExpireAsync(str, TimeSpan.MaxValue)));
    probes.Add(await Probe("KeyExpire.AtMaxValue", d => d.KeyExpireAsync(str, DateTime.MaxValue)));

    probes.Add(await Probe("SortedSetRange.Ascending", d => d.SortedSetRangeByRankAsync(zset)));
    probes.Add(await Probe("SortedSetRange.Descending", d => d.SortedSetRangeByRankAsync(zset, 0, -1, Order.Descending)));

    // ExecuteAsync names the command the caller passed, including a command the server rejects.
    probes.Add(await Probe("Execute.Ping", static async d => { await d.ExecuteAsync("PING"); }));
    probes.Add(await Probe("Execute.LowerCase", static async d => { await d.ExecuteAsync("ping"); }));
    probes.Add(await Probe("Execute.Unknown", static async d => { await d.ExecuteAsync("QYLNOSUCH"); }));

    // Delete last: the server may answer this one with UNLINK.
    probes.Add(await Probe("KeyDelete", d => d.KeyDeleteAsync(str2)));
}

QylDemoOtlp.Flush(tracerProvider);

var report = RedisReport.Create(
    RuntimeFeature.IsDynamicCodeSupported ? "dynamic-code-supported" : "nativeaot",
    probes.ToArray());

var json = JsonSerializer.Serialize(report, RealRedisJsonContext.Default.RedisReport);
Console.WriteLine(json);

return report.Pass ? 0 : 1;

static async Task<ConnectionMultiplexer> WaitForRedisAsync(string configuration)
{
    Exception? lastException = null;

    for (var attempt = 0; attempt < 60; attempt++)
    {
        try
        {
            return await ConnectionMultiplexer.ConnectAsync(configuration + ",connectTimeout=2000,abortConnect=true");
        }
        catch (RedisConnectionException exception)
        {
            lastException = exception;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new InvalidOperationException("Redis did not become ready.", lastException);
}

internal sealed record CapturedActivity(
    string TraceId,
    string SpanId,
    string Name,
    string Kind,
    string Status,
    IReadOnlyDictionary<string, string> Tags)
{
    public static CapturedActivity From(Activity activity)
        => new(
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.DisplayName,
            activity.Kind.ToString(),
            activity.Status.ToString(),
            activity.TagObjects.ToDictionary(
                static tag => tag.Key,
                static tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparer.Ordinal));
}

internal sealed record CommandProbe(
    string Label,
    string[] WireCommands,
    CapturedActivity[] Spans,
    string? ServerError);

/// <summary>One probe and the one span it produced, named by its trace and span id.</summary>
internal sealed record ProbeResult(
    string TraceId,
    string SpanId,
    string Label,
    string WireCommand,
    string SpanOperation,
    string SpanStatus);

internal sealed record RedisReport(
    string RuntimeMode,
    bool Pass,
    string[] Failures,
    ProbeResult[] Probes)
{
    /// <summary>
    /// The call sites whose span deliberately does not name the wire command, keyed by probe so
    /// that no other probe's comparison is loosened. Each one pins the exact wire command it
    /// tolerates, so a change in what StackExchange.Redis sends still fails.
    /// A delete accepts either command because the choice belongs to the server: UNLINK is
    /// substituted for DEL wherever it is supported, which a compile-time interceptor cannot see.
    /// The <c>ValueCondition.NotExists</c> set is a deliberate inaccuracy rather than an unknown:
    /// it reaches SETNX and the interceptor could say so, but <c>ValueCondition</c> carries
    /// neither an equality operator nor <c>IEquatable</c>, so the test would box on every
    /// intercepted SET including the calls made with tracing switched off.
    /// </summary>
    private static readonly Dictionary<string, string[]> s_acceptedWireCommands = new(StringComparer.Ordinal)
    {
        ["KeyDelete"] = ["DEL", "UNLINK"],
        ["StringSet.NotExists"] = ["SETNX"],
    };

    public static RedisReport Create(string runtimeMode, CommandProbe[] probes)
    {
        var failures = new List<string>();
        var results = new List<ProbeResult>();

        foreach (var probe in probes)
        {
            var spans = probe.Spans
                .Where(static span =>
                    span.Tags.TryGetValue("qyl.instrumentation.domain", out var domain) &&
                    StringComparer.Ordinal.Equals(domain, "db.redis"))
                .ToArray();

            if (spans.Length != 1)
            {
                failures.Add($"{probe.Label}: expected 1 Redis span, got {spans.Length}");
                continue;
            }

            if (probe.WireCommands.Length != 1)
            {
                failures.Add($"{probe.Label}: expected 1 wire command, got [{string.Join(", ", probe.WireCommands)}]");
                continue;
            }

            var span = spans[0];
            var wire = probe.WireCommands[0];

            if (!StringComparer.Ordinal.Equals(span.Kind, "Client"))
                failures.Add($"{probe.Label}: expected kind Client, got {span.Kind}");

            RequireTag(
                probe.Label,
                span,
                Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes.SystemName,
                Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Db.DbAttributes.SystemNameValues.Redis,
                failures);

            // The demo connects with GetDatabase() (the default database), so db.namespace is "0".
            RequireTag(
                probe.Label,
                span,
                Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes.Namespace,
                "0",
                failures);

            if (!span.Tags.TryGetValue(
                    Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes.OperationName,
                    out var operation))
            {
                failures.Add($"{probe.Label}: span is missing db.operation.name (wire sent {wire})");
                results.Add(new ProbeResult(span.TraceId, span.SpanId, probe.Label, wire, string.Empty, span.Status));
                continue;
            }

            // The registry's db.client name chain: {db.operation.name} {db.namespace}, the namespace being
            // the database index asserted above.
            var expectedName = operation + " 0";
            if (!StringComparer.Ordinal.Equals(span.Name, expectedName))
                failures.Add($"{probe.Label}: expected span name {expectedName}, got {span.Name}");

            var accepted = s_acceptedWireCommands.TryGetValue(probe.Label, out var declared)
                ? declared
                : [operation];

            if (!accepted.Contains(wire, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{probe.Label}: span reported db.operation.name={operation} but the wire command was {wire}; " +
                    $"accepted [{string.Join(", ", accepted)}]");
            }

            RequireTag(
                probe.Label,
                span,
                Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes.QuerySummary,
                operation,
                failures);

            var expectedStatus = probe.ServerError is null ? "Unset" : "Error";
            if (!StringComparer.Ordinal.Equals(span.Status, expectedStatus))
                failures.Add($"{probe.Label}: expected status {expectedStatus}, got {span.Status}");

            if (probe.ServerError is not null)
            {
                RequireTag(
                    probe.Label,
                    span,
                    Qyl.Telemetry.SemanticConventions.Attributes.Error.ErrorAttributes.Type,
                    typeof(RedisServerException).FullName!,
                    failures);
            }

            results.Add(new ProbeResult(span.TraceId, span.SpanId, probe.Label, wire, operation, span.Status));
        }

        if (probes.Length != results.Count)
            failures.Add($"expected every probe to produce a result, got {results.Count} of {probes.Length}");

        if (!results.Any(static result => StringComparer.Ordinal.Equals(result.SpanStatus, "Error")))
            failures.Add("expected at least one Redis span carrying a server error");

        return new RedisReport(runtimeMode, failures.Count is 0, failures.ToArray(), results.ToArray());
    }

    private static void RequireTag(
        string label,
        CapturedActivity activity,
        string key,
        string expected,
        ICollection<string> failures)
    {
        if (!activity.Tags.TryGetValue(key, out var actual))
        {
            failures.Add($"{label}: missing {key}");
            return;
        }

        if (!StringComparer.Ordinal.Equals(actual, expected))
            failures.Add($"{label}: expected {key}={expected}, got {actual}");
    }
}

[JsonSerializable(typeof(RedisReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class RealRedisJsonContext : JsonSerializerContext;
