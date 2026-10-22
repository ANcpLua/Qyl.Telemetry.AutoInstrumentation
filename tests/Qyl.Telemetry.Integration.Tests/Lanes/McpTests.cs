using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// The official ModelContextProtocol client/server sources over a pipe: discovery, tools/list and a
/// tools/call, each a client/server pair on one trace under the qyl root, with stable and distinct
/// session ids and no sensitive tool value in the output. The registration on and toggled off,
/// managed and NativeAOT.
/// </summary>
[TestClass]
[TestCategory("mcp")]
public sealed class McpTests(TestContext testContext)
{
    private const string McpSource = "Experimental.ModelContextProtocol";
    private static readonly string[] SensitiveValues = ["mcp-sensitive-argument-73f8a9", "mcp-sensitive-result-9d20c4"];

    private static readonly Dictionary<string, string> Operations = new(StringComparer.Ordinal)
    {
        ["server/discover"] = "server/discover",
        ["tools/list"] = "tools/list",
        ["tools/call qyl_sensitive_probe"] = "tools/call",
    };

    [TestMethod]
    public async Task ExportsExpectedTelemetry()
    {
        var lane = new DemoContext(DemoLanes.ByName("mcp"), testContext);
        var disabled = lane.With(("OTEL_DOTNET_AUTO_TRACES_MCP_INSTRUMENTATION_ENABLED", "false"));
        lane.BuildManaged("--disable-build-servers");
        Verify(lane, (await lane.RunManagedAsync("managed MCP demo")), registrationEnabled: true);
        Verify(lane, (await lane.RunManagedAsync("managed MCP demo (registration disabled)", disabled)), registrationEnabled: false);

        var executable = lane.PublishNativeAot();
        Verify(lane, (await lane.RunNativeAotAsync(executable, "NativeAOT MCP demo")), registrationEnabled: true);
        Verify(lane, (await lane.RunNativeAotAsync(executable, "NativeAOT MCP demo (registration disabled)", disabled)), registrationEnabled: false);
    }

    private static void Verify(DemoContext lane, DemoRun run, bool registrationEnabled)
    {
        run.RequireSucceeded();
        foreach (var sensitive in SensitiveValues)
            Assert.IsFalse(run.Stdout.Contains(sensitive, StringComparison.Ordinal), $"{run.Label} leaked a sensitive tool value to telemetry output: {sensitive}");
        run.RequireReportPassed()
            .RequireBool("ToolListed", true)
            .RequireBool("ToolCalled", true)
            .RequireBool("QylRegistrationEnabled", registrationEnabled);

        var activities = run.RequireCount("Activities", registrationEnabled ? 6 : 0, "MCP activities");
        lane.Telemetry.Spans(run, activities, "MCP");
        if (!registrationEnabled)
            return;

        var all = activities.EnumerateArray().ToArray();
        Assert.IsTrue(all.All(static activity => ReportedActivity.Text(activity, "Source") == McpSource), $"{run.Label} ActivitySource mismatch:\n{activities}");
        var names = all.GroupBy(static activity => ReportedActivity.Text(activity, "Name") ?? string.Empty).ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        Assert.IsTrue(names.Count == Operations.Count && Operations.Keys.All(name => names.GetValueOrDefault(name) == 2), $"{run.Label} operation set mismatch:\n{activities}");

        var rootTrace = run.StringField("RootTraceId");
        var rootSpan = run.StringField("RootSpanId");
        Assert.IsTrue(rootTrace is not null && rootSpan is not null, $"{run.Label} report is missing root context:\n{run.Dump()}");

        var sessions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { ["Client"] = [], ["Server"] = [] };
        foreach (var (operation, method) in Operations)
        {
            var pair = all.Where(activity => ReportedActivity.Text(activity, "Name") == operation).ToArray();
            var byKind = pair.ToDictionary(static activity => ReportedActivity.Text(activity, "Kind") ?? string.Empty, StringComparer.Ordinal);
            Assert.IsTrue(byKind.Count == 2 && byKind.ContainsKey("Client") && byKind.ContainsKey("Server"), $"{run.Label} activity pair mismatch for {operation}:\n{string.Join("\n", pair)}");
            var client = byKind["Client"];
            var server = byKind["Server"];
            RequireTags(run, client, ("mcp.method.name", method), ("network.transport", "pipe"), ("session.id", "qyl-mcp-evidence-session"));
            RequireTags(run, server, ("mcp.method.name", method), ("network.transport", "pipe"));

            Assert.IsTrue(ReportedActivity.Text(client, "TraceId") == rootTrace && ReportedActivity.Text(client, "ParentSpanId") == rootSpan,
                $"{run.Label} client activity lost its qyl parent for {operation}: {client}");
            Assert.IsTrue(ReportedActivity.Text(server, "TraceId") == ReportedActivity.Text(client, "TraceId") && ReportedActivity.Text(server, "ParentSpanId") == ReportedActivity.Text(client, "SpanId"),
                $"{run.Label} server activity did not continue client context for {operation}");

            foreach (var (kind, activity) in byKind)
            {
                var session = ReportedActivity.Tag(activity, "mcp.session.id");
                Assert.IsFalse(string.IsNullOrEmpty(session), $"{run.Label} {kind} activity is missing mcp.session.id: {activity}");
                sessions[kind].Add(session!);
            }
        }

        Assert.IsTrue(sessions.Values.All(static values => values.Count == 1), $"{run.Label} session identifiers were not stable");
        Assert.IsFalse(sessions["Client"].SetEquals(sessions["Server"]), $"{run.Label} client/server sessions were not distinct");
        foreach (var activity in all.Where(static activity => ReportedActivity.Text(activity, "Name") == "tools/call qyl_sensitive_probe"))
            RequireTags(run, activity, ("gen_ai.tool.name", "qyl_sensitive_probe"), ("gen_ai.operation.name", "execute_tool"), ("mcp.protocol.version", "2026-07-28"));
    }

    private static void RequireTags(DemoRun run, JsonElement activity, params (string Key, string Value)[] expected)
    {
        foreach (var (key, value) in expected)
            Assert.AreEqual(value, ReportedActivity.Tag(activity, key), $"{run.Label} MCP activity tag mismatch for {key}: expected={value} activity={activity}");
    }
}
