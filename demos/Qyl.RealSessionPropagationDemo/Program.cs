using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Qyl;
using QylTelemetryNames = Qyl.Telemetry.SemanticConventions.Names.QylTelemetryNames;
using SessionAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Session.SessionAttributes;

// qyl's OWN behaviour, not a library's. `session.id` is the one key qyl moves by itself, in two
// ways: QylSessionSpanProcessor copies it from the nearest tagged IN-PROCESS ancestor onto a
// descendant that carries none, and AddQyl's ASP.NET Core middleware stamps the request's W3C
// baggage member `session.id` onto the server span. qyl writes no baggage: the session crosses a
// process boundary only because the application put it there.
//
// One executable, two roles. The two roles run as two real operating-system processes, and the
// PROCESSES ARE STARTED BY tests/Qyl.Telemetry.Integration.Tests/Lanes/SessionPropagationTests.cs ALONE — this program never
// starts itself, and never starts any other process. Two hosts inside one process would leave
// Activity.Parent linked across the "boundary" and the third property below would pass while being
// false; that is the measurement error this demo exists to avoid.
var role = ArgumentValue(args, "--role");
var rawPort = ArgumentValue(args, "--port");
if (!int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
    throw new ArgumentException($"--port is not a port number: {rawPort}", nameof(args));

return role switch
{
    SessionDemo.DownstreamRole => await RunDownstreamAsync(port),
    SessionDemo.UpstreamRole => await RunUpstreamAsync(port),
    _ => throw new ArgumentException($"unknown --role {role}", nameof(args)),
};

static string ArgumentValue(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    if (index < 0 || index + 1 >= arguments.Length)
        throw new ArgumentException($"missing {name} argument", nameof(arguments));

    return arguments[index + 1];
}

/// <summary>
/// The process that owns the session. It tags a root span, starts the two in-process descendants
/// the first two properties are about, and makes one real HTTP request to the downstream process.
/// </summary>
static async Task<int> RunUpstreamAsync(int port)
{
    var exported = new List<Activity>();
    var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
    {
        ApplicationName = SessionDemo.ApplicationName,
        DisableDefaults = true,
    });
    builder.AddQyl(options =>
    {
        options.ServiceName = "qyl-real-session-propagation-demo-upstream";
        options.EnableLogExport = false;
        options.EnableMetricsExport = false;
        // The processor under test. Every other demo turns it off; this one is about it.
        options.EnableSessionTagInheritance = true;
    });
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddInMemoryExporter(exported));

    using var host = builder.Build();
    await host.StartAsync();

    using (var httpClient = new HttpClient())
    using (var root = Spans.Work(SessionDemo.UpstreamRootSpanName))
    {
        root!.SetTag(SessionAttributes.Id, SessionDemo.UpstreamSessionId);

        // The application's own doing, and the ONLY way the value reaches the next process: it puts
        // the session in baggage on purpose. qyl writes no baggage; downstream, AddQyl alone (no
        // Qyl.Api) turns the arrived member into the server span's session.id.
        root.AddBaggage(SessionAttributes.Id, SessionDemo.UpstreamSessionId);

        // Property 1: an in-process descendant with no session of its own inherits the ancestor's.
        using (Spans.Work(SessionDemo.UpstreamInheritedSpanName))
        {
        }

        // Property 2: a descendant that already carries a session keeps its own.
        using (var own = Spans.Work(SessionDemo.UpstreamOwnSessionSpanName))
        {
            own!.SetTag(SessionAttributes.Id, SessionDemo.UpstreamOwnSessionId);
        }

        using var response = await httpClient.GetAsync(
            $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{SessionDemo.ProbePath}");
        Console.WriteLine("probe-status=" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
    }

    host.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);
    await host.StopAsync();

    return Emit(SessionPropagationReport.CreateUpstream(RuntimeMode(), exported));
}

/// <summary>
/// The process that receives the request. Its own session-tag inheritance is ON, and it proves that in
/// the same run with a locally rooted control pair, so the absence of <c>session.id</c> on the
/// remote-parented server span cannot be explained by a missing processor.
/// </summary>
static async Task<int> RunDownstreamAsync(int port)
{
    var exported = new List<Activity>();
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
    builder.Logging.ClearProviders();
    builder.Services.AddHealthChecks();
    builder.AddQyl(options =>
    {
        options.ServiceName = "qyl-real-session-propagation-demo-downstream";
        options.EnableLogExport = false;
        options.EnableMetricsExport = false;
        options.EnableSessionTagInheritance = true;
    });
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddInMemoryExporter(exported));

    var app = builder.Build();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

    app.MapHealthChecks("/healthz");
    app.MapGet(SessionDemo.ProbePath, (HttpContext context) =>
    {
        // A child of the SERVER span: it inherits the session the server span was stamped with.
        using (Spans.Work(SessionDemo.DownstreamProbeChildSpanName))
        {
        }

        // The run ends when the one probe has been answered. The stop is requested after the
        // response is complete, so no loop waits for it and nothing is left running.
        context.Response.OnCompleted(static state =>
        {
            ((IHostApplicationLifetime)state).StopApplication();
            return Task.CompletedTask;
        }, lifetime);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    });

    await app.StartAsync();
    // The lane starts the upstream once this line appears: Kestrel is bound and accepting.
    Console.WriteLine("downstream-listening=" + port.ToString(CultureInfo.InvariantCulture));

    // The in-process control, in THIS process: a locally rooted span carrying its own session and a
    // child that inherits it. A downstream whose processor was simply absent would otherwise
    // "prove" the remote boundary by proving nothing at all.
    using (var localRoot = Spans.Work(SessionDemo.DownstreamLocalRootSpanName))
    {
        localRoot!.SetTag(SessionAttributes.Id, SessionDemo.DownstreamSessionId);

        using (Spans.Work(SessionDemo.DownstreamLocalChildSpanName))
        {
        }
    }

    await app.WaitForShutdownAsync();

    app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);

    return Emit(SessionPropagationReport.CreateDownstream(RuntimeMode(), exported));
}

static string RuntimeMode() => RuntimeFeature.IsDynamicCodeSupported ? "dynamic-code-supported" : "nativeaot";

static int Emit(SessionPropagationReport report)
{
    Console.WriteLine(JsonSerializer.Serialize(
        report,
        RealSessionPropagationJsonContext.Default.SessionPropagationReport));

    return report.Pass ? 0 : 1;
}

/// <summary>The demo's application name and span scope, its role names, its routes and the four session values.</summary>
internal static class SessionDemo
{
    internal const string ApplicationName = "Qyl.RealSessionPropagationDemo";

    /// <summary>The application scope its own spans start on through <c>Spans.Work</c>.</summary>
    internal const string SourceName = QylTelemetryNames.Scopes.QylTelemetryApplication;
    internal const string AspNetCoreSourceName = "Microsoft.AspNetCore";
    internal const string HttpClientSourceName = "System.Net.Http";

    internal const string UpstreamRole = "upstream";
    internal const string DownstreamRole = "downstream";

    internal const string ProbePath = "/probe";

    internal const string UpstreamRootSpanName = "upstream root";
    internal const string UpstreamInheritedSpanName = "upstream inherits session";
    internal const string UpstreamOwnSessionSpanName = "upstream owns session";
    internal const string UpstreamClientSpanName = "GET";
    internal const string DownstreamServerSpanName = "GET " + ProbePath;
    internal const string DownstreamProbeChildSpanName = "downstream probe child";
    internal const string DownstreamLocalRootSpanName = "downstream local root";
    internal const string DownstreamLocalChildSpanName = "downstream local child";

    /// <summary>The session the upstream root span carries, and the one its descendants inherit.</summary>
    internal const string UpstreamSessionId = "qyl-real-session-propagation-demo-session";

    /// <summary>The session a descendant brought itself. It must survive untouched.</summary>
    internal const string UpstreamOwnSessionId = "qyl-real-session-propagation-demo-own-session";

    /// <summary>The downstream process's own, locally rooted session: the in-process control.</summary>
    internal const string DownstreamSessionId = "qyl-real-session-propagation-demo-downstream-session";
}

internal sealed record CapturedActivity(
    string Source,
    string Name,
    string Kind,
    string TraceId,
    string SpanId,
    string ParentSpanId,
    bool HasInProcessParent,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, string> Baggage)
{
    public string Tuple => Source + "/" + Name;

    public static CapturedActivity From(Activity activity)
        => new(
            activity.Source.Name,
            activity.DisplayName,
            activity.Kind.ToString(),
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.ParentSpanId.ToHexString(),
            // The whole difference between the two processes: in-process ancestors are objects the
            // processor can walk, a remote parent is an id and nothing else.
            activity.Parent is not null,
            Flatten(activity.TagObjects),
            Flatten(activity.Baggage));

    private static Dictionary<string, string> Flatten<T>(IEnumerable<KeyValuePair<string, T>> pairs)
    {
        var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
            flattened[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;

        return flattened;
    }
}

internal sealed record SessionPropagationReport(
    string Role,
    string RuntimeMode,
    bool Pass,
    string[] Failures,
    int ProcessId,
    bool SessionTagInheritanceEnabled,
    string[] ExpectedSpans,
    string[] ActualSpans,
    CapturedActivity[] Activities)
{
    public static SessionPropagationReport CreateUpstream(string runtimeMode, List<Activity> exported)
    {
        var failures = new List<string>();
        var activities = exported.Select(CapturedActivity.From).ToArray();

        // The exact multiset the upstream produces: its three own spans plus the BCL's client span.
        // A fourth qyl span, or a missing one, is a contract change and fails here.
        var expected = new[]
        {
            SessionDemo.SourceName + "/" + SessionDemo.UpstreamRootSpanName,
            SessionDemo.SourceName + "/" + SessionDemo.UpstreamInheritedSpanName,
            SessionDemo.SourceName + "/" + SessionDemo.UpstreamOwnSessionSpanName,
            SessionDemo.HttpClientSourceName + "/" + SessionDemo.UpstreamClientSpanName,
        };
        var actual = RequireExactSpans(activities, expected, failures);

        var root = Single(activities, SessionDemo.SourceName, SessionDemo.UpstreamRootSpanName, failures);
        var inherited = Single(activities, SessionDemo.SourceName, SessionDemo.UpstreamInheritedSpanName, failures);
        var own = Single(activities, SessionDemo.SourceName, SessionDemo.UpstreamOwnSessionSpanName, failures);
        var client = Single(activities, SessionDemo.HttpClientSourceName, SessionDemo.UpstreamClientSpanName, failures);

        RequireTag(root, SessionAttributes.Id, SessionDemo.UpstreamSessionId, failures);

        // PROPERTY 1. The in-process descendant that brought no session carries the ancestor's.
        RequireTag(inherited, SessionAttributes.Id, SessionDemo.UpstreamSessionId, failures);
        RequireInProcessParent(inherited, failures);

        // PROPERTY 2. The descendant that brought its own keeps it; the ancestor does not win.
        RequireTag(own, SessionAttributes.Id, SessionDemo.UpstreamOwnSessionId, failures);
        RequireInProcessParent(own, failures);

        // The BCL's client span is an in-process descendant like any other, so it inherits too —
        // and it is the span the downstream's server span parents to.
        RequireTag(client, SessionAttributes.Id, SessionDemo.UpstreamSessionId, failures);

        // The application put the session in baggage; that is what leaves this process.
        RequireBaggage(client, SessionAttributes.Id, SessionDemo.UpstreamSessionId, failures);

        RequireOneTrace(activities, failures);

        return new SessionPropagationReport(
            SessionDemo.UpstreamRole,
            runtimeMode,
            failures.Count is 0,
            failures.ToArray(),
            Environment.ProcessId,
            SessionTagInheritanceEnabled: true,
            expected.Order(StringComparer.Ordinal).ToArray(),
            actual,
            activities);
    }

    public static SessionPropagationReport CreateDownstream(string runtimeMode, List<Activity> exported)
    {
        var failures = new List<string>();
        var activities = exported.Select(CapturedActivity.From).ToArray();

        var expected = new[]
        {
            SessionDemo.AspNetCoreSourceName + "/" + SessionDemo.DownstreamServerSpanName,
            SessionDemo.SourceName + "/" + SessionDemo.DownstreamProbeChildSpanName,
            SessionDemo.SourceName + "/" + SessionDemo.DownstreamLocalRootSpanName,
            SessionDemo.SourceName + "/" + SessionDemo.DownstreamLocalChildSpanName,
        };
        var actual = RequireExactSpans(activities, expected, failures);

        var server = Single(activities, SessionDemo.AspNetCoreSourceName, SessionDemo.DownstreamServerSpanName, failures);
        var probeChild = Single(activities, SessionDemo.SourceName, SessionDemo.DownstreamProbeChildSpanName, failures);
        var localRoot = Single(activities, SessionDemo.SourceName, SessionDemo.DownstreamLocalRootSpanName, failures);
        var localChild = Single(activities, SessionDemo.SourceName, SessionDemo.DownstreamLocalChildSpanName, failures);

        // The control: in THIS process, in-process propagation demonstrably works. Without this
        // pair, a downstream with no processor at all would satisfy the property below by accident.
        RequireTag(localRoot, SessionAttributes.Id, SessionDemo.DownstreamSessionId, failures);
        RequireTag(localChild, SessionAttributes.Id, SessionDemo.DownstreamSessionId, failures);
        RequireInProcessParent(localChild, failures);

        // PROPERTY 3. The server span's parent is remote: an id arrived over the wire and no
        // ancestor object exists, so no processor can copy anything onto it. The session reaches it
        // in baggage, and AddQyl's middleware stamps the baggage member onto the server span: the
        // session spine, with no Qyl.Api in this process.
        if (server is not null)
        {
            if (server.HasInProcessParent)
                failures.Add($"{server.Tuple} has an in-process parent: the two roles did not run in two processes");
            if (StringComparer.Ordinal.Equals(server.ParentSpanId, EmptySpanId))
                failures.Add($"{server.Tuple} has no remote parent: the request carried no traceparent");
            if (!StringComparer.Ordinal.Equals(server.Kind, "Server"))
                failures.Add($"expected kind Server on {server.Tuple}, got {server.Kind}");
        }

        RequireBaggage(server, SessionAttributes.Id, SessionDemo.UpstreamSessionId, failures);
        RequireTag(server, SessionAttributes.Id, SessionDemo.UpstreamSessionId, failures);

        // The stamped server span is an in-process ancestor of the probe child, which inherits it.
        RequireTag(probeChild, SessionAttributes.Id, SessionDemo.UpstreamSessionId, failures);

        return new SessionPropagationReport(
            SessionDemo.DownstreamRole,
            runtimeMode,
            failures.Count is 0,
            failures.ToArray(),
            Environment.ProcessId,
            SessionTagInheritanceEnabled: true,
            expected.Order(StringComparer.Ordinal).ToArray(),
            actual,
            activities);
    }

    private const string EmptySpanId = "0000000000000000";

    private static string[] RequireExactSpans(
        CapturedActivity[] activities,
        string[] expected,
        ICollection<string> failures)
    {
        var actual = activities.Select(static activity => activity.Tuple).Order(StringComparer.Ordinal).ToArray();
        var sortedExpected = expected.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(sortedExpected, StringComparer.Ordinal))
        {
            failures.Add(
                $"span set mismatch: expected [{string.Join(", ", sortedExpected)}], " +
                $"got [{string.Join(", ", actual)}]");
        }

        return actual;
    }

    private static CapturedActivity? Single(
        CapturedActivity[] activities,
        string source,
        string name,
        ICollection<string> failures)
    {
        var matches = activities
            .Where(activity =>
                StringComparer.Ordinal.Equals(activity.Source, source) &&
                StringComparer.Ordinal.Equals(activity.Name, name))
            .ToArray();

        if (matches.Length is 1)
            return matches[0];

        failures.Add(
            $"expected exactly 1 span {source}/{name}, got {matches.Length.ToString(CultureInfo.InvariantCulture)}");
        return null;
    }

    private static void RequireTag(
        CapturedActivity? activity,
        string key,
        string expected,
        ICollection<string> failures)
    {
        if (activity is null)
            return;

        if (!activity.Tags.TryGetValue(key, out var actual))
        {
            failures.Add($"{activity.Tuple} is missing {key}");
            return;
        }

        if (!StringComparer.Ordinal.Equals(actual, expected))
            failures.Add($"{activity.Tuple} expected {key}={expected}, got {actual}");
    }

    private static void RequireBaggage(
        CapturedActivity? activity,
        string key,
        string expected,
        ICollection<string> failures)
    {
        if (activity is null)
            return;

        if (!activity.Baggage.TryGetValue(key, out var actual))
        {
            failures.Add($"{activity.Tuple} is missing baggage {key}");
            return;
        }

        if (!StringComparer.Ordinal.Equals(actual, expected))
            failures.Add($"{activity.Tuple} expected baggage {key}={expected}, got {actual}");
    }

    private static void RequireInProcessParent(CapturedActivity? activity, ICollection<string> failures)
    {
        if (activity is not null && !activity.HasInProcessParent)
            failures.Add($"{activity.Tuple} has no in-process parent to inherit from");
    }

    private static void RequireOneTrace(CapturedActivity[] activities, ICollection<string> failures)
    {
        var traces = activities
            .Select(static activity => activity.TraceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (traces.Length is not 1)
            failures.Add($"expected one trace, got [{string.Join(", ", traces.Order(StringComparer.Ordinal))}]");
    }
}

[JsonSerializable(typeof(SessionPropagationReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class RealSessionPropagationJsonContext : JsonSerializerContext;
