namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// The StackExchange.Redis interceptor against the testbed Redis, managed and NativeAOT. The demo
/// compares every span with the wire command; the lane also pins the commands whose mapping is not a
/// plain method-name lookup, so dropping one of those call sites cannot shrink the evidence.
/// </summary>
[TestClass]
[TestCategory("redis")]
public sealed class RedisTests(TestContext testContext)
{
    private static readonly Dictionary<string, string> RequiredProbes = new(StringComparer.Ordinal)
    {
        ["StringGet"] = "GET",
        ["StringGet.Multi"] = "MGET",
        ["HashGet.Single"] = "HGET",
        ["HashGet.Multi"] = "HMGET",
        ["SetContains.Single"] = "SISMEMBER",
        ["SetContains.Multi"] = "SMISMEMBER",
        ["HashIncrement.Float"] = "HINCRBYFLOAT",
        ["HashSet.Entries"] = "HMSET",
        // Reports SET although the wire receives SETNX: see s_acceptedWireCommands in the demo.
        ["StringSet.NotExists"] = "SET",
        ["StringSet.WhenNotExists"] = "SET",
        ["HashSet.Field"] = "HSET",
        ["HashSet.FieldNotExists"] = "HSETNX",
        ["StringIncrement.Unit"] = "INCR",
        ["StringIncrement.By"] = "INCRBY",
        ["StringIncrement.Float"] = "INCRBYFLOAT",
        ["StringDecrement.Unit"] = "DECR",
        ["StringDecrement.By"] = "DECRBY",
        ["StringIncrement.MinusOne"] = "DECR",
        ["StringIncrement.Negative"] = "DECRBY",
        ["StringDecrement.MinusOne"] = "INCR",
        ["StringDecrement.Negative"] = "INCRBY",
        ["SortedSetLength"] = "ZCARD",
        ["SortedSetLength.Bounded"] = "ZCOUNT",
        ["ListLeftPush"] = "LPUSH",
        ["ListLeftPush.Exists"] = "LPUSHX",
        ["ListRightPush.Exists"] = "RPUSHX",
        ["SortedSetRange.Ascending"] = "ZRANGE",
        ["SortedSetRange.Descending"] = "ZREVRANGE",
        ["KeyExpire.Seconds"] = "EXPIRE",
        ["KeyExpire.Milliseconds"] = "PEXPIRE",
        ["KeyExpire.SubMillisecondTicks"] = "EXPIRE",
        ["KeyExpire.Persist"] = "PERSIST",
        ["KeyExpire.At"] = "EXPIREAT",
        ["KeyExpire.AtMilliseconds"] = "PEXPIREAT",
        ["KeyExpire.AtPersist"] = "PERSIST",
        ["KeyExpire.MaxValue"] = "PERSIST",
        ["KeyExpire.AtMaxValue"] = "PERSIST",
        ["Execute.Ping"] = "PING",
        ["Execute.LowerCase"] = "PING",
        ["Execute.Unknown"] = "QYLNOSUCH",
        ["KeyTimeToLive"] = "PTTL",
    };

    [TestMethod]
    public async Task ExportsExpectedTelemetry()
    {
        var lane = new DemoContext(DemoLanes.ByName("redis"), testContext);
        lane.BuildManaged();
        Verify(lane, (await lane.RunManagedAsync("managed Redis demo")));
        Verify(lane, (await lane.RunNativeAotAsync(lane.PublishNativeAot(), "NativeAOT Redis demo")));
    }

    private static void Verify(DemoContext lane, DemoRun run)
    {
        run.RequireSucceeded().RequireReportPassed();
        var probes = run.RequireAtLeast("Probes", RequiredProbes.Count, "Redis probes").EnumerateArray().ToArray();
        var observed = probes.GroupBy(static probe => ReportedActivity.Text(probe, "Label") ?? string.Empty)
            .ToDictionary(static group => group.Key, static group => ReportedActivity.Text(group.Last(), "SpanOperation"), StringComparer.Ordinal);
        foreach (var (label, operation) in RequiredProbes)
        {
            Assert.IsTrue(observed.ContainsKey(label), $"{run.Label} missing required probe {label}\n{run.Dump()}");
            Assert.AreEqual(operation, observed[label], $"{run.Label} probe {label} reported db.operation.name={observed[label]}, expected {operation}");
        }

        // Each probe names its one span; the collector must hold it with the same db.operation.name.
        lane.Telemetry.Spans(run, LaneChecks.SpanReferences(probes.Select(static probe => (
            ReportedActivity.Text(probe, "TraceId")!,
            ReportedActivity.Text(probe, "SpanId")!,
            (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["db.operation.name"] = ReportedActivity.Text(probe, "SpanOperation")! }))), "Redis");
    }
}
