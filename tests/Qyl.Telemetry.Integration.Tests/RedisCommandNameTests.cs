using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class RedisCommandNameTests(TestContext testContext)
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Qyl.Telemetry.AutoInstrumentation.Generated")]);

    private const string RedisSurface = """
using System;
using System.Threading.Tasks;

namespace StackExchange.Redis
{
    public readonly struct RedisKey
    {
        public static implicit operator RedisKey(string key) => default;
    }

    [Flags]
    public enum CommandFlags { None = 0 }

    [Flags]
    public enum Exclude { None = 0 }

    public interface IDatabaseAsync
    {
        int Database { get; }
        Task<long> StringIncrementAsync(RedisKey key, long value = 1, CommandFlags flags = CommandFlags.None);
        Task<long> StringDecrementAsync(RedisKey key, long value = 1, CommandFlags flags = CommandFlags.None);
        Task<long> SortedSetLengthAsync(RedisKey key, double min = double.NegativeInfinity, double max = double.PositiveInfinity, Exclude exclude = Exclude.None, CommandFlags flags = CommandFlags.None);
        Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, CommandFlags flags = CommandFlags.None);
        Task<bool> KeyExpireAsync(RedisKey key, DateTime? expiry, CommandFlags flags = CommandFlags.None);
    }
}

internal sealed class AnsweringDatabase : StackExchange.Redis.IDatabaseAsync
{
    public int Database => 0;
    public Task<long> StringIncrementAsync(StackExchange.Redis.RedisKey key, long value = 1, StackExchange.Redis.CommandFlags flags = StackExchange.Redis.CommandFlags.None) => Task.FromResult(0L);
    public Task<long> StringDecrementAsync(StackExchange.Redis.RedisKey key, long value = 1, StackExchange.Redis.CommandFlags flags = StackExchange.Redis.CommandFlags.None) => Task.FromResult(0L);
    public Task<long> SortedSetLengthAsync(StackExchange.Redis.RedisKey key, double min = double.NegativeInfinity, double max = double.PositiveInfinity, StackExchange.Redis.Exclude exclude = StackExchange.Redis.Exclude.None, StackExchange.Redis.CommandFlags flags = StackExchange.Redis.CommandFlags.None) => Task.FromResult(0L);
    public Task<bool> KeyExpireAsync(StackExchange.Redis.RedisKey key, TimeSpan? expiry, StackExchange.Redis.CommandFlags flags = StackExchange.Redis.CommandFlags.None) => Task.FromResult(true);
    public Task<bool> KeyExpireAsync(StackExchange.Redis.RedisKey key, DateTime? expiry, StackExchange.Redis.CommandFlags flags = StackExchange.Redis.CommandFlags.None) => Task.FromResult(true);
}
""";

    [TestMethod]
    public async Task StringIncrementOrDecrement_NegativeValue_ReportsWireCommand()
    {
        var operations = await RunAsync("""
            await database.StringIncrementAsync("orders", -1);
            await database.StringIncrementAsync("orders", -5);
            await database.StringDecrementAsync("orders", -1);
            await database.StringDecrementAsync("orders", -5);
""");
        Assert.AreSequenceEqual(["DECR", "DECRBY", "INCR", "INCRBY"], operations);
    }

    [TestMethod]
    public async Task SortedSetLength_BoundedRange_ReportsZcount()
    {
        var operations = await RunAsync("""
            await database.SortedSetLengthAsync("scores");
            await database.SortedSetLengthAsync("scores", 1, 10);
""");
        Assert.AreSequenceEqual(["ZCARD", "ZCOUNT"], operations);
    }

    [TestMethod]
    public async Task KeyExpire_MaxValueExpiry_ReportsPersist()
    {
        var operations = await RunAsync("""
            await database.KeyExpireAsync("orders", TimeSpan.MaxValue);
            await database.KeyExpireAsync("orders", DateTime.MaxValue);
""");
        Assert.AreSequenceEqual(["PERSIST", "PERSIST"], operations);
    }

    private async Task<string?[]> RunAsync(string calls)
    {
        var compilation = Generate(RedisSurface, $$"""
using System;
using System.Threading.Tasks;
using StackExchange.Redis;

internal static class Consumer
{
    internal static async Task Run(IDatabaseAsync database)
    {
{{calls}}
    }
}
""");
        var assembly = Load(compilation);
        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == QylActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var database = Activator.CreateInstance(assembly.GetType("AnsweringDatabase", throwOnError: true)!);
        var run = assembly.GetType("Consumer", throwOnError: true)!.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)run.Invoke(null, [database])!;
        return [.. started.Select(static span => span.GetTagItem("db.operation.name") as string)];
    }

    private CSharpCompilation Generate(params string[] sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(QylGeneratedCodeAbi).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("RedisCommandNameConsumer",
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, ParseOptions, $"Consumer{index}.cs", cancellationToken: testContext.CancellationToken)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new QylAutoInstrumentationGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, testContext.CancellationToken);
        return (CSharpCompilation)updated;
    }

    private static Assembly Load(Compilation compilation)
    {
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        return emitted.Success
            ? Assembly.Load(image.ToArray())
            : throw new InvalidOperationException("The generated consumer did not emit:" + Environment.NewLine + string.Join(Environment.NewLine, emitted.Diagnostics));
    }
}
