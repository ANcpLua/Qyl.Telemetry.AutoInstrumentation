namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>Grpc.Net.Client's native source: the raw CallInvoker shape, the ClientBase shape, and the
/// unconfigured metadata contract, each managed and NativeAOT.</summary>
[TestClass]
[TestCategory("grpc-client")]
public sealed class GrpcClientTests(TestContext testContext)
{
    [TestMethod]
    public async Task ExportsExpectedTelemetry()
    {
        var lane = new DemoContext(DemoLanes.ByName("grpc-client"), testContext);
        lane.BuildManaged();
        var executable = lane.PublishNativeAot();
        Verify(lane, (await lane.RunManagedAsync("managed gRPC client demo")));
        Verify(lane, (await lane.RunNativeAotAsync(executable, "NativeAOT gRPC client demo")));

        // ClientBase<T> and the raw CallInvoker share one native owner; metadata is opt-in in both.
        var client = lane.With(("QYL_GRPC_DEMO_MODE", "client"));
        Verify(lane, (await lane.RunManagedAsync("managed gRPC client demo (client lane)", client)));
        Verify(lane, (await lane.RunNativeAotAsync(executable, "NativeAOT gRPC client demo (client lane)", client)));

    }

    private static void Verify(DemoContext lane, DemoRun run)
    {
        run.RequireSucceeded().RequireTokens("expected-failure=Unavailable").RequireReportPassed();
        var activities = run.Array("Activities");
        var grpc = activities.EnumerateArray().Count(static activity => ReportedActivity.Tag(activity, "qyl.instrumentation.domain") == "rpc.grpc");
        Assert.AreEqual(2, grpc, $"{run.Label} expected exactly 2 gRPC client activities, got {grpc}:\n{activities}");
        lane.Telemetry.Spans(run, activities, "gRPC client");
    }
}
