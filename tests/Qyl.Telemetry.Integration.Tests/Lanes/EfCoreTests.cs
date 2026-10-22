using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>EF Core's DiagnosticListener lane against SQLite, managed and NativeAOT (compiled model,
/// a vendor-warned publish), with and without the statement-text opt-in. The demo requires
/// db.query.text on its spans when the opt-in is set, and its absence otherwise.</summary>
[TestClass]
[TestCategory("efcore")]
sealed class EfCoreTests : SpanDemoLaneTests<EfCoreTests>, ISpanDemoLane
{
    private const string ManagedStatementOptIn = "managed-statement-opt-in";
    private const string NativeAotStatementOptIn = "nativeaot-statement-opt-in";
    private const string ExpectedFailure = "expected-failure=SqliteException";
    private const int ReportedActivities = 3;

    private static readonly (string, string) StatementOptIn = ("OTEL_DOTNET_AUTO_ENTITYFRAMEWORKCORE_SET_DBSTATEMENT_FOR_TEXT", "true");

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.NativeAot(DemoRuns.NativeAot),
        DemoVariant.Managed(ManagedStatementOptIn, StatementOptIn),
        DemoVariant.NativeAot(NativeAotStatementOptIn, StatementOptIn),
    ];

    public static string LaneName => "efcore";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, ExpectedFailure);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsThreeActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
