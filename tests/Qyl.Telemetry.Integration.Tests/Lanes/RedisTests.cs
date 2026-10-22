using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// The StackExchange.Redis interceptor against the testbed Redis, managed and NativeAOT. The demo
/// compares every span with the wire command; the lane also pins the commands whose mapping is not a
/// plain method-name lookup, so dropping one of those call sites cannot shrink the evidence. Each probe
/// names its one span, and the collector must hold it with the same db.operation.name.
/// </summary>
[TestClass]
[TestCategory("redis")]
sealed class RedisTests : SpanDemoLaneTests<RedisTests>, ISpanDemoLane
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

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "redis";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> ProbeRows => Rows(RunKeys, RequiredProbes.Keys);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) =>
        LaneReport.SpanReferences(run.Array("Probes").Select(static probe => (
            ReportedActivity.Text(probe, "TraceId")!,
            ReportedActivity.Text(probe, "SpanId")!,
            (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["db.operation.name"] = ReportedActivity.Text(probe, "SpanOperation")! })));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsAtLeastEveryRequiredProbe(string run) =>
        Assert.IsGreaterThanOrEqualTo(RequiredProbes.Count, Run(run).Array("Probes").Length);

    [TestMethod]
    [DynamicData(nameof(ProbeRows))]
    public void RedisProbe_EachRequiredCall_ReportsItsOperationName(string run, string probe) =>
        Assert.AreEqual(RequiredProbes[probe], Run(run).Array("Probes")
            .Where(reported => ReportedActivity.Text(reported, "Label") == probe)
            .Select(static reported => ReportedActivity.Text(reported, "SpanOperation"))
            .LastOrDefault(), Run(run).Dump());
}
