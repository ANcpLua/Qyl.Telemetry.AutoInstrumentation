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
sealed class McpTests : SpanDemoLaneTests<McpTests>, ISpanDemoLane
{
    private const string ManagedRegistrationDisabled = "managed-registration-disabled";
    private const string NativeAotRegistrationDisabled = "nativeaot-registration-disabled";
    private const string McpSource = "Experimental.ModelContextProtocol";
    private const string SensitiveArgument = "mcp-sensitive-argument-73f8a9";
    private const string SensitiveResult = "mcp-sensitive-result-9d20c4";
    private const string ToolListed = "ToolListed";
    private const string ToolCalled = "ToolCalled";
    private const string Discover = "server/discover";
    private const string ToolsList = "tools/list";
    private const string ToolsCall = "tools/call qyl_sensitive_probe";
    private const string RootTraceId = "RootTraceId";
    private const string RootSpanId = "RootSpanId";
    private const string Client = "Client";
    private const string Server = "Server";
    private const int EnabledActivities = 6;

    private static readonly Dictionary<string, string> Methods = new(StringComparer.Ordinal)
    {
        [Discover] = "server/discover",
        [ToolsList] = "tools/list",
        [ToolsCall] = "tools/call",
    };

    private static readonly (string, string) RegistrationOff = ("OTEL_DOTNET_AUTO_TRACES_MCP_INSTRUMENTATION_ENABLED", "false");

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.Managed(ManagedRegistrationDisabled, RegistrationOff),
        DemoVariant.NativeAot(DemoRuns.NativeAot),
        DemoVariant.NativeAot(NativeAotRegistrationDisabled, RegistrationOff),
    ];

    private static readonly string[] EnabledRuns = [DemoRuns.Managed, DemoRuns.NativeAot];

    public static string LaneName => "mcp";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> SensitiveValues => Rows(RunKeys, SensitiveArgument, SensitiveResult);

    public static IEnumerable<object[]> ToolSteps => Rows(RunKeys, ToolListed, ToolCalled);

    public static IEnumerable<object[]> EnabledRunRows => EnabledRuns.Select(static run => new object[] { run });

    public static IEnumerable<object[]> RootContextFields => Rows(EnabledRuns, RootTraceId, RootSpanId);

    public static IEnumerable<object[]> EnabledOperations => Rows(EnabledRuns, Methods.Keys);

    public static IEnumerable<object[]> EnabledSides => Rows(EnabledRuns, Client, Server);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants, "--disable-build-servers");

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(SensitiveValues))]
    public void DemoOutput_EachRun_LeaksNoSensitiveToolValue(string run, string sensitive) =>
        Assert.DoesNotContain(sensitive, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(ToolSteps))]
    public void DemoReport_EachRun_ReportsTheToolStep(string run, string step) =>
        Assert.IsTrue(Run(run).BoolField(step), Run(run).Dump());

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(ManagedRegistrationDisabled, false)]
    [DataRow(DemoRuns.NativeAot, true)]
    [DataRow(NativeAotRegistrationDisabled, false)]
    public void DemoReport_EachRun_ReportsWhetherRegistrationIsEnabled(string run, bool registered) =>
        Assert.AreEqual(registered, Run(run).BoolField("QylRegistrationEnabled"));

    [TestMethod]
    [DataRow(DemoRuns.Managed, EnabledActivities)]
    [DataRow(ManagedRegistrationDisabled, 0)]
    [DataRow(DemoRuns.NativeAot, EnabledActivities)]
    [DataRow(NativeAotRegistrationDisabled, 0)]
    public void DemoReport_EachRun_ReportsActivitiesOnlyWhenRegistered(string run, int activities) =>
        Assert.HasCount(activities, Run(run).Array("Activities"));

    [TestMethod]
    [DynamicData(nameof(EnabledRunRows))]
    public void McpSpans_RegistrationEnabled_ComeFromTheMcpSource(string run) =>
        Assert.AreSequenceEqual(Run(run).Array("Activities").Select(static _ => McpSource),
            Run(run).Array("Activities").Select(static activity => ReportedActivity.Text(activity, "Source")));

    [TestMethod]
    [DynamicData(nameof(EnabledRunRows))]
    public void McpSpans_RegistrationEnabled_AreTwoPerOperation(string run) =>
        Assert.AreSequenceEqual(Methods.Keys.SelectMany(static operation => new[] { operation, operation }),
            Run(run).Array("Activities").Select(static activity => ReportedActivity.Text(activity, "Name")), SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(RootContextFields))]
    public void DemoReport_RegistrationEnabled_NamesTheRootContext(string run, string field) =>
        Assert.IsNotNull(Run(run).StringField(field), Run(run).Dump());

    [TestMethod]
    [DynamicData(nameof(EnabledOperations))]
    public void McpOperation_RegistrationEnabled_IsOneClientAndOneServerSpan(string run, string operation) =>
        Assert.AreSequenceEqual([Client, Server], Pair(run, operation).Select(static activity => ReportedActivity.Text(activity, "Kind")),
            SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(EnabledOperations))]
    public void McpClientSpan_RegistrationEnabled_CarriesMethodTransportAndSession(string run, string operation)
    {
        var client = Side(run, operation, Client);

        Assert.AreSequenceEqual([Methods[operation], "pipe", "qyl-mcp-evidence-session"],
            new[] { ReportedActivity.Tag(client, "mcp.method.name"), ReportedActivity.Tag(client, "network.transport"), ReportedActivity.Tag(client, "session.id") });
    }

    [TestMethod]
    [DynamicData(nameof(EnabledOperations))]
    public void McpServerSpan_RegistrationEnabled_CarriesMethodAndTransport(string run, string operation)
    {
        var server = Side(run, operation, Server);

        Assert.AreSequenceEqual([Methods[operation], "pipe"],
            new[] { ReportedActivity.Tag(server, "mcp.method.name"), ReportedActivity.Tag(server, "network.transport") });
    }

    [TestMethod]
    [DynamicData(nameof(EnabledOperations))]
    public void McpClientSpan_RegistrationEnabled_IsAChildOfTheQylRoot(string run, string operation)
    {
        var client = Side(run, operation, Client);

        Assert.AreEqual((Run(run).StringField(RootTraceId), Run(run).StringField(RootSpanId)),
            (ReportedActivity.Text(client, "TraceId"), ReportedActivity.Text(client, "ParentSpanId")));
    }

    [TestMethod]
    [DynamicData(nameof(EnabledOperations))]
    public void McpServerSpan_RegistrationEnabled_ContinuesTheClientContext(string run, string operation)
    {
        var client = Side(run, operation, Client);
        var server = Side(run, operation, Server);

        Assert.AreEqual((ReportedActivity.Text(client, "TraceId"), ReportedActivity.Text(client, "SpanId")),
            (ReportedActivity.Text(server, "TraceId"), ReportedActivity.Text(server, "ParentSpanId")));
    }

    [TestMethod]
    [DynamicData(nameof(EnabledRunRows))]
    public void McpSpans_RegistrationEnabled_CarryASessionId(string run) =>
        Assert.DoesNotContain(static activity => string.IsNullOrEmpty(ReportedActivity.Tag(activity, "mcp.session.id")), Run(run).Array("Activities"));

    [TestMethod]
    [DynamicData(nameof(EnabledSides))]
    public void McpSessions_RegistrationEnabled_StayStablePerSide(string run, string side) =>
        Assert.ContainsSingle(Sessions(run, side));

    [TestMethod]
    [DynamicData(nameof(EnabledRunRows))]
    public void McpSessions_RegistrationEnabled_DifferBetweenClientAndServer(string run) =>
        Assert.AreNotEqual(string.Join(",", Sessions(run, Client)), string.Join(",", Sessions(run, Server)));

    [TestMethod]
    [DynamicData(nameof(EnabledRunRows))]
    public void McpToolCallSpans_RegistrationEnabled_CarryTheGenAiToolAttributes(string run) =>
        Assert.AreSequenceEqual(Pair(run, ToolsCall).SelectMany(static _ => new[] { "qyl_sensitive_probe", "execute_tool", "2026-07-28" }),
            Pair(run, ToolsCall).SelectMany(static activity => new[]
            {
                ReportedActivity.Tag(activity, "gen_ai.tool.name"),
                ReportedActivity.Tag(activity, "gen_ai.operation.name"),
                ReportedActivity.Tag(activity, "mcp.protocol.version"),
            }));

    private static JsonElement[] Pair(string run, string operation) =>
        [.. Run(run).Array("Activities").Where(activity => ReportedActivity.Text(activity, "Name") == operation)];

    private static JsonElement Side(string run, string operation, string kind) =>
        Pair(run, operation).First(activity => ReportedActivity.Text(activity, "Kind") == kind);

    private static string[] Sessions(string run, string kind) =>
        [.. Methods.Keys.SelectMany(operation => Pair(run, operation))
            .Where(activity => ReportedActivity.Text(activity, "Kind") == kind)
            .Select(static activity => ReportedActivity.Tag(activity, "mcp.session.id") ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
