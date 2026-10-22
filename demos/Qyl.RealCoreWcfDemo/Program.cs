using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.ServiceModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreWCF.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Qyl;
using ClientBasicHttpBinding = System.ServiceModel.BasicHttpBinding;
using ClientEndpointAddress = System.ServiceModel.EndpointAddress;
using RpcAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Rpc.RpcAttributes;
using ServerAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Server.ServerAttributes;
using ServerBasicHttpBinding = CoreWCF.BasicHttpBinding;

var exportedActivities = new List<Activity>();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddHealthChecks();
builder.Services.AddServiceModelServices();
builder.AddQyl(options =>
{
    options.ServiceName = "qyl-real-corewcf-demo";
    options.EnableLogExport = false;
    options.EnableMetricsExport = false;
});
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddInMemoryExporter(exportedActivities));

// Stopped and disposed after an explicit, bounded flush: the Demos and LiveCheck targets read
// what this demo exports, and host disposal alone shuts the batch processor down with Timeout.Infinite —
// against a listener that has gone away this demo would wait on it rather than report.
await using var app = builder.Build();
app.MapHealthChecks("/healthz");
app.UseServiceModel(services =>
{
    services.AddService<ProbeService>();
    services.AddServiceEndpoint<ProbeService, IProbeService>(new ServerBasicHttpBinding(), "/probe.svc");
});

await app.StartAsync();
using var coreWcfSubscriptionProbe = new ActivitySource("CoreWCF.Primitives");
var registrationObserved = coreWcfSubscriptionProbe.HasListeners();

try
{
    var endpoint = new ClientEndpointAddress(app.Urls.Single() + "/probe.svc");
    await using var factory = new ChannelFactory<IProbeService>(new ClientBasicHttpBinding(), endpoint);
    var channel = factory.CreateChannel();

    var response = channel.Echo("secret-payload");
    if (!StringComparer.Ordinal.Equals(response, "echo-ok"))
        throw new InvalidOperationException($"unexpected CoreWCF response: {response}");

    var expectedFaultObserved = false;
    try
    {
        channel.Fail();
    }
    catch (FaultException)
    {
        expectedFaultObserved = true;
    }

    if (!expectedFaultObserved)
        throw new InvalidOperationException("CoreWCF failure operation did not return a SOAP fault");

    ((IClientChannel)channel).Close();
}
finally
{
    app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);
    await app.StopAsync();
}

var registrationEnabled = !string.Equals(
    Environment.GetEnvironmentVariable("OTEL_DOTNET_AUTO_TRACES_WCFCORE_INSTRUMENTATION_ENABLED"),
    "false",
    StringComparison.OrdinalIgnoreCase);
var report = CoreWcfReport.Create(exportedActivities, registrationEnabled, registrationObserved);
Console.WriteLine(JsonSerializer.Serialize(report, CoreWcfJsonContext.Default.CoreWcfReport));
return report.Pass ? 0 : 1;

[CoreWCF.ServiceContract]
[System.ServiceModel.ServiceContract]
public interface IProbeService
{
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    string Echo(string value);

    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    void Fail();
}

public sealed class ProbeService : IProbeService
{
    public string Echo(string value) => value.Length > 0 ? "echo-ok" : "echo-empty";

    public void Fail() => throw new InvalidOperationException("expected-corewcf-failure");
}

internal sealed record CapturedActivity(
    string TraceId,
    string SpanId,
    string Source,
    string Name,
    string Kind,
    string Status,
    IReadOnlyDictionary<string, string> Tags)
{
    internal static CapturedActivity From(Activity activity)
        => new(
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.Source.Name,
            activity.DisplayName,
            activity.Kind.ToString(),
            activity.Status.ToString(),
            activity.TagObjects.ToDictionary(
                static tag => tag.Key,
                static tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparer.Ordinal));
}

internal sealed record CoreWcfReport(
    bool Pass,
    string RuntimeMode,
    string[] Failures,
    bool QylRegistrationEnabled,
    bool QylRegistrationObserved,
    CapturedActivity[] Activities)
{
    internal static CoreWcfReport Create(
        IEnumerable<Activity> activities,
        bool registrationEnabled,
        bool registrationObserved)
    {
        var failures = new List<string>();
        var coreWcfActivities = activities
            .Where(static activity => StringComparer.Ordinal.Equals(activity.Source.Name, "CoreWCF.Primitives"))
            .Select(CapturedActivity.From)
            .ToArray();

        var expectedCount = registrationEnabled ? 2 : 0;
        if (registrationObserved != registrationEnabled)
            failures.Add($"CoreWCF source registration mismatch: expected {registrationEnabled}, observed {registrationObserved}");
        if (coreWcfActivities.Length != expectedCount)
            failures.Add($"expected {expectedCount} CoreWCF server spans, got {coreWcfActivities.Length}");

        if (!registrationEnabled)
            return new CoreWcfReport(
                failures.Count is 0,
                CurrentRuntimeMode(),
                failures.ToArray(),
                false,
                registrationObserved,
                coreWcfActivities);

        RequireOperation(coreWcfActivities, nameof(IProbeService.Echo), failures);
        RequireOperation(coreWcfActivities, nameof(IProbeService.Fail), failures);

        foreach (var activity in coreWcfActivities)
        {
            if (!StringComparer.Ordinal.Equals(activity.Kind, nameof(ActivityKind.Server)))
                failures.Add($"expected CoreWCF Server activity, got {activity.Kind}");
            // CoreWCF writes the deprecated key and qyl exports it unchanged; the collector's
            // AttributeMapping.TryGetRename is what rewrites it to rpc.system.name at ingest.
            RequireTag(activity, "rpc.system", "dotnet_wcf", failures);
            RequireMissingTag(activity, RpcAttributes.SystemName, failures);
            RequirePresentTag(activity, ServerAttributes.Address, failures);
            RequirePresentTag(activity, ServerAttributes.Port, failures);
            RequireTag(activity, "wcf.channel.path", "/probe.svc", failures);

            if (activity.Tags.TryGetValue(RpcAttributes.Method, out var rpcMethod))
            {
                var expectedStatus = rpcMethod.EndsWith("/Fail", StringComparison.Ordinal) ? "Error" : "Unset";
                if (!StringComparer.Ordinal.Equals(activity.Status, expectedStatus))
                    failures.Add($"expected {rpcMethod} status {expectedStatus}, got {activity.Status}");
            }

            if (activity.Name.Contains("secret-payload", StringComparison.Ordinal) ||
                activity.Tags.Values.Any(static value => value.Contains("secret-payload", StringComparison.Ordinal)))
            {
                failures.Add("CoreWCF telemetry captured the request payload");
            }
        }

        return new CoreWcfReport(
            failures.Count is 0,
            CurrentRuntimeMode(),
            failures.ToArray(),
            true,
            registrationObserved,
            coreWcfActivities);
    }

    private static string CurrentRuntimeMode() =>
        RuntimeFeature.IsDynamicCodeSupported ? "dynamic-code-supported" : "nativeaot";

    private static void RequireOperation(
        IEnumerable<CapturedActivity> activities,
        string method,
        ICollection<string> failures)
    {
        if (!activities.Any(activity =>
                activity.Tags.TryGetValue(RpcAttributes.Method, out var actual) &&
                actual.EndsWith("/" + method, StringComparison.Ordinal)))
        {
            failures.Add($"missing CoreWCF rpc.method={method} span");
        }
    }

    private static void RequireTag(
        CapturedActivity activity,
        string key,
        string expected,
        ICollection<string> failures)
    {
        if (!activity.Tags.TryGetValue(key, out var actual))
        {
            failures.Add($"missing {key}");
            return;
        }

        if (!StringComparer.Ordinal.Equals(actual, expected))
            failures.Add($"expected {key}={expected}, got {actual}");
    }

    private static void RequirePresentTag(
        CapturedActivity activity,
        string key,
        ICollection<string> failures)
    {
        if (!activity.Tags.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            failures.Add($"missing {key}");
    }

    private static void RequireMissingTag(
        CapturedActivity activity,
        string key,
        ICollection<string> failures)
    {
        if (activity.Tags.ContainsKey(key))
            failures.Add($"unexpected deprecated {key}");
    }
}

[JsonSerializable(typeof(CoreWcfReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class CoreWcfJsonContext : JsonSerializerContext;
