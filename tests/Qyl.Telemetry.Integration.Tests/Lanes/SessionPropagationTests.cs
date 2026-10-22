using System.Globalization;
using System.Text.Json;
using Qyl.Telemetry.Tests;
using SessionAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Session.SessionAttributes;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// Runs the session-propagation demo as TWO REAL PROCESSES and holds qyl to its own contract:
/// (1) an in-process descendant of a span carrying session.id inherits it, (2) a descendant that
/// already carries its own is not overwritten, (3) across a real HTTP boundary into a second process
/// the server span carries the session.id that reached it in baggage — stamped by AddQyl's ASP.NET Core
/// middleware, no Qyl.Api involved — on the same trace with a remote parent, and its in-process child
/// inherits it. This lane, never the demo binary, starts both processes, and each report has to name
/// the process id the lane launched; a shared in-memory exporter, or one process reporting for both
/// roles, fails there. The downstream role's locally rooted control proves its processor works on a
/// span that no baggage reached. The downstream stops itself once the one probe has been answered.
/// </summary>
[TestClass]
[TestCategory("session-propagation")]
sealed class SessionPropagationTests : SpanDemoLaneTests<SessionPropagationTests>, ISpanDemoLane
{
    private const string Upstream = "upstream";
    private const string Downstream = "downstream";
    private const string DemoSource = "Qyl.Telemetry.Application";
    private const string AspNetCoreSource = "Microsoft.AspNetCore";
    private const string HttpClientSource = "System.Net.Http";
    private const string UpstreamRoot = "upstream root";
    private const string UpstreamInherits = "upstream inherits session";
    private const string UpstreamOwns = "upstream owns session";
    private const string ClientGet = "GET";
    private const string ServerGetProbe = "GET /probe";
    private const string DownstreamProbeChild = "downstream probe child";
    private const string DownstreamLocalRoot = "downstream local root";
    private const string DownstreamLocalChild = "downstream local child";
    private const string SessionTag = SessionAttributes.Id;
    private const string SessionId = "qyl-real-session-propagation-demo-session";
    private const string OwnSessionId = "qyl-real-session-propagation-demo-own-session";
    private const string DownstreamSessionId = "qyl-real-session-propagation-demo-downstream-session";
    private const string EmptySpanId = "0000000000000000";
    private const string ProbeAnswered = "probe-status=204";
    private const string DownstreamListening = "downstream-listening=";

    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(30);

    public static string LaneName => "session-propagation";

    public static IReadOnlyList<string> RunKeys => [Upstream, Downstream];

    public static async Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane)
    {
        lane.BuildManaged();
        var port = Shell.FreeTcpPort().ToString(CultureInfo.InvariantCulture);
        await using var receiver = await OtlpReceiver.StartAsync(lane.CancellationToken);
        var environment = lane.With(("QYL_ENDPOINT", receiver.Endpoint.ToString().TrimEnd('/')));
        ProcessResult upstream;
        ProcessResult downstream;
        using (var downstreamProcess = lane.StartManaged(["--role", Downstream, "--port", port], environment))
        {
            await OutputWait.ForTokenAsync(() => ProcessObservation.Of(downstreamProcess), DownstreamListening + port, TimeProvider.System, ListenTimeout,
                lane.CancellationToken);
            using var upstreamProcess = lane.StartManaged(["--role", Upstream, "--port", port], environment);
            upstream = await upstreamProcess.WaitForExitAsync(TimeSpan.FromSeconds(120), lane.CancellationToken);
            downstream = await downstreamProcess.WaitForExitAsync(TimeSpan.FromSeconds(60), lane.CancellationToken);
        }

        var capture = lane.RecordCapture("two-process session propagation", receiver.Snapshot(), upstream, downstream);
        return new Dictionary<string, DemoRun>(StringComparer.Ordinal)
        {
            [Upstream] = new(lane.Lane.Name, Upstream, LaneRuntime.Managed, upstream, capture),
            [Downstream] = new(lane.Lane.Name, Downstream, LaneRuntime.Managed, downstream, capture),
        };
    }

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    public void DemoOutput_Upstream_PrintsTheAnsweredProbe() =>
        Assert.Contains(ProbeAnswered, Run(Upstream).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DataRow(Upstream)]
    [DataRow(Downstream)]
    public void DemoReport_EachRole_NamesItsRole(string role) =>
        Assert.AreEqual(role, Run(role).StringField("Role"));

    [TestMethod]
    [DataRow(Upstream)]
    [DataRow(Downstream)]
    public void DemoReport_EachRole_RunsWithSessionTagInheritance(string role) =>
        Assert.AreEqual(true, Run(role).BoolField("SessionTagInheritanceEnabled"));

    [TestMethod]
    [DataRow(Upstream)]
    [DataRow(Downstream)]
    public void DemoReport_EachRole_NamesTheProcessIdTheLaneLaunched(string role) =>
        Assert.AreEqual(Run(role).Process.ProcessId, Run(role).IntField("ProcessId"));

    [TestMethod]
    public void DemoProcesses_TwoRoles_HaveDistinctProcessIds() =>
        Assert.AreNotEqual(Run(Downstream).Process.ProcessId, Run(Upstream).Process.ProcessId);

    [TestMethod]
    [DataRow(Upstream, DemoSource, UpstreamRoot)]
    [DataRow(Upstream, DemoSource, UpstreamInherits)]
    [DataRow(Upstream, DemoSource, UpstreamOwns)]
    [DataRow(Upstream, HttpClientSource, ClientGet)]
    [DataRow(Downstream, AspNetCoreSource, ServerGetProbe)]
    [DataRow(Downstream, DemoSource, DownstreamProbeChild)]
    [DataRow(Downstream, DemoSource, DownstreamLocalRoot)]
    [DataRow(Downstream, DemoSource, DownstreamLocalChild)]
    public void ReportedSpan_EachContractSpan_IsReportedExactlyOnce(string role, string source, string name) =>
        Assert.ContainsSingle(ReportedActivity.Named(Run(role).Array("Activities"), source, name));

    [TestMethod]
    [DataRow(Upstream, DemoSource, UpstreamRoot, SessionId)]
    [DataRow(Upstream, DemoSource, UpstreamInherits, SessionId)]
    [DataRow(Upstream, DemoSource, UpstreamOwns, OwnSessionId)]
    [DataRow(Upstream, HttpClientSource, ClientGet, SessionId)]
    [DataRow(Downstream, AspNetCoreSource, ServerGetProbe, SessionId)]
    [DataRow(Downstream, DemoSource, DownstreamProbeChild, SessionId)]
    [DataRow(Downstream, DemoSource, DownstreamLocalRoot, DownstreamSessionId)]
    [DataRow(Downstream, DemoSource, DownstreamLocalChild, DownstreamSessionId)]
    public void ReportedSpan_SessionTag_IsTheExpectedSession(string role, string source, string name, string session) =>
        Assert.AreEqual(session, ReportedActivity.Tag(Span(role, source, name), SessionTag));

    [TestMethod]
    [DataRow(Upstream, DemoSource, UpstreamInherits, true)]
    [DataRow(Downstream, AspNetCoreSource, ServerGetProbe, false)]
    [DataRow(Downstream, DemoSource, DownstreamLocalChild, true)]
    public void ReportedSpan_Parent_IsInProcessOnlyWithinOneProcess(string role, string source, string name, bool inProcess) =>
        Assert.AreEqual(inProcess, ReportedActivity.Bool(Span(role, source, name), "HasInProcessParent"));

    [TestMethod]
    public void UpstreamClientSpan_Trace_IsTheRootTrace() =>
        Assert.AreEqual(Text(Upstream, DemoSource, UpstreamRoot, "TraceId"), Text(Upstream, HttpClientSource, ClientGet, "TraceId"));

    [TestMethod]
    public void DownstreamServerSpan_Trace_IsTheUpstreamClientTrace() =>
        Assert.AreEqual(Text(Upstream, HttpClientSource, ClientGet, "TraceId"), Text(Downstream, AspNetCoreSource, ServerGetProbe, "TraceId"));

    [TestMethod]
    public void DownstreamServerSpan_Parent_IsTheUpstreamClientSpan() =>
        Assert.AreEqual(Text(Upstream, HttpClientSource, ClientGet, "SpanId"), Text(Downstream, AspNetCoreSource, ServerGetProbe, "ParentSpanId"));

    [TestMethod]
    public void DownstreamServerSpan_Parent_IsNotEmpty() =>
        Assert.AreNotEqual(EmptySpanId, Text(Downstream, AspNetCoreSource, ServerGetProbe, "ParentSpanId"));

    [TestMethod]
    public void DownstreamServerSpan_Baggage_CarriesTheApplicationSession() =>
        Assert.AreEqual(SessionId, Span(Downstream, AspNetCoreSource, ServerGetProbe).TryGetProperty("Baggage", out var baggage)
                                   && baggage.ValueKind is JsonValueKind.Object && baggage.TryGetProperty(SessionTag, out var session)
            ? session.GetString()
            : null);

    [TestMethod]
    public void DownstreamLocalChild_Trace_IsNotTheRequestTrace() =>
        Assert.AreNotEqual(Text(Downstream, AspNetCoreSource, ServerGetProbe, "TraceId"), Text(Downstream, DemoSource, DownstreamLocalChild, "TraceId"));

    private static JsonElement Span(string role, string source, string name) =>
        ReportedActivity.Named(Run(role).Array("Activities"), source, name).Single();

    private static string? Text(string role, string source, string name, string field) => ReportedActivity.Text(Span(role, source, name), field);
}
