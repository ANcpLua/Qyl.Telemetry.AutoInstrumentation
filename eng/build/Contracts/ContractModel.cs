using Item = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace Qyl.Build.Contracts;

/// <summary>A conformance profile: one fixture service and the conformance signals it is expected to emit.</summary>
sealed record ConformanceProfile(string ServiceName, string ProfileId, string[] SignalNames);

/// <summary>
/// The resolved AOT contract: the upstream OpenTelemetry .NET automatic-instrumentation rows, the
/// qyl-native rows and the qyl ownership overlay, merged and held to the model's invariants.
/// </summary>
static class ContractModel
{
    public static AbsolutePath UpstreamContractPath => Repo.ContractsDirectory / "otel-dotnet-auto.upstream.yaml";
    public static AbsolutePath QylNativeContractPath => Repo.ContractsDirectory / "qyl-native-instrumentations.yaml";
    public static AbsolutePath OwnershipPath => Repo.ContractsDirectory / "qyl-aot-ownership.yaml";
    public static AbsolutePath ResolvedContractPath => Repo.ContractsDirectory / "generated" / "qyl-aot-contract.resolved.yaml";
    public static AbsolutePath SchemaPath => Repo.ContractsDirectory / "generated" / "qyl-aot-contract.schema.json";
    public static AbsolutePath ConformancePlanPath => Repo.ContractsDirectory / "qyl-aot-autoinstrumentation.conformance-plan.json";

    public const string UpstreamOrigin = "upstream_otel_dotnet_auto_60";
    public const string QylNativeOrigin = "qyl_native";

    // The upstream row count is bookkeeping of what open-telemetry/opentelemetry-dotnet-instrumentation
    // publishes, not a qyl invariant: when upstream adds a row the contract grows, so it is read from the
    // contract. What is enforced is what a count never proved: the indexes run contiguously from 1, and
    // the contract_item_id sequence matches them exactly.
    private static int? _upstreamCount;

    public static int UpstreamContractItemCount
    {
        get
        {
            if (_upstreamCount is { } cached)
                return cached;
            var document = Yaml.Load(UpstreamContractPath) as Item;
            var items = document?.Get("contract_items") as List<object?>;
            Gate.Require(items is { Count: > 0 }, $"contract: {UpstreamContractPath.Name} carries no contract_items");
            return (_upstreamCount = items!.Count).Value;
        }
    }

    // This one stays a literal on purpose: the six qyl-native promises are qyl's own scope.
    private const int QylNativeContractItemCount = 6;

    public static int TotalContractItemCount => UpstreamContractItemCount + QylNativeContractItemCount;

    public static readonly ConformanceProfile[] ConformanceProfiles =
    [
        new("qyl-webapi-aot-demo", "qyl-aot-webapi", ["aspnetcore.server", "httpclient.downstream", "httpclient.self"]),
        new("qyl-db-aot-demo", "qyl-aot-db", ["adonet.command", "efcore.sqlite", "mysqlconnector.command", "npgsql.command", "oraclemda.command", "sqlite.command", "sqlclient.command"]),
        new("qyl-grpc-aot-demo", "qyl-aot-grpc", ["grpc.client"]),
        new("qyl-azure-aot-demo", "qyl-aot-azure", ["azure.sdk"]),
        new("qyl-search-aot-demo", "qyl-aot-search", ["{db.operation}", "{http.request.method}"]),
        new("qyl-graphql-aot-demo", "qyl-aot-graphql", ["graphql.execute"]),
        new("qyl-messaging-aot-demo", "qyl-aot-messaging", ["{destination} send", "publish {messaging.rabbitmq.destination.routing_key}"]),
        new("qyl-cache-aot-demo", "qyl-aot-cache", ["redis.command"]),
        new("qyl-logging-aot-demo", "qyl-aot-logging", ["ilogger.log"]),
        new("qyl-mcp-aot-demo", "qyl-aot-mcp", ["mcp.initialize", "mcp.notifications.initialized", "mcp.tools.list", "mcp.tools.call"]),
        new("qyl-metrics-aot-demo", "qyl-aot-metrics",
        [
            "aspnetcore.components.render_diff.duration",
            "aspnetcore.components.render_diff.size",
            "aspnetcore.components.update_parameters.duration",
            "http.client.request.duration",
            "db.client.operation.duration",
            "dotnet.gc.collections",
            "dotnet.gc.last_collection.heap.size",
            "dotnet.thread_pool.thread.count",
            "dotnet.process.cpu.time",
            "dotnet.process.memory.working_set",
            "dotnet.process.cpu.count",
        ]),
        new("qyl-unsupported-nativeaot-demo", "qyl-aot-unsupported-nativeaot", []),
    ];

    public static readonly HashSet<string> RequiredConformanceProfileIds = new(StringComparer.Ordinal)
    {
        "qyl-aot-azure", "qyl-aot-cache", "qyl-aot-db", "qyl-aot-graphql", "qyl-aot-grpc", "qyl-aot-logging",
        "qyl-aot-mcp", "qyl-aot-messaging", "qyl-aot-metrics", "qyl-aot-search", "qyl-aot-unsupported-nativeaot", "qyl-aot-webapi",
    };

    public const string SignalKind = "signal_specific_instrumentation_promise";
    public const string ControlKind = "global_environment_control";
    public const string OptionKind = "instrumentation_option";

    public static readonly HashSet<string> Lanes = new(StringComparer.Ordinal)
    {
        "source_interceptor",
        "runtime_public_telemetry",
        // The library owns the ActivitySource; Qyl.Telemetry.Hosting subscribes it and
        // QylNativeSpanProcessor stamps qyl.instrumentation.domain onto its spans. Nothing is
        // intercepted and nothing the library wrote is rewritten.
        "native_source",
        "official_library_hook",
        "environment_control",
        "instrumentation_option",
        "unsupported_nativeaot",
        // An upstream promise qyl binds through no mechanism at all — not the same claim as
        // unsupported_nativeaot, which asserts the substrate makes the promise unreachable.
        "not_implemented",
    };

    public static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "implemented", "control_bound", "option_bound", "research_required", "unsupported_nativeaot" };
    public static readonly HashSet<string> CallSiteVisibilities = new(StringComparer.Ordinal) { "user_code", "library_internal", "both", "not_applicable" };
    public static readonly HashSet<string> PayloadAccessValues = new(StringComparer.Ordinal) { "typed_public", "aot_reflection", "reflection_required", "not_applicable" };
    public static readonly HashSet<string> EvidenceLevels = new(StringComparer.Ordinal) { "none", "verified_nativeaot", "verified_managed", "compile_binding_only", "option_bound" };
    public static readonly HashSet<string> ConformanceKinds = new(StringComparer.Ordinal) { "span", "metric", "log" };

    private static readonly (string Id, string Key, string Signal, string InstrumentationId, string Toggle)[] QylNative8SignalContract =
    [
        ("QYL_NATIVE_CONTRACT_001", "signals.traces.MICROSOFTEXTENSIONSAI", "traces", "MICROSOFTEXTENSIONSAI", "OTEL_DOTNET_AUTO_TRACES_MICROSOFTEXTENSIONSAI_INSTRUMENTATION_ENABLED"),
        ("QYL_NATIVE_CONTRACT_002", "signals.metrics.MICROSOFTEXTENSIONSAI", "metrics", "MICROSOFTEXTENSIONSAI", "OTEL_DOTNET_AUTO_METRICS_MICROSOFTEXTENSIONSAI_INSTRUMENTATION_ENABLED"),
        ("QYL_NATIVE_CONTRACT_003", "signals.traces.MICROSOFTAGENTSAI", "traces", "MICROSOFTAGENTSAI", "OTEL_DOTNET_AUTO_TRACES_MICROSOFTAGENTSAI_INSTRUMENTATION_ENABLED"),
        ("QYL_NATIVE_CONTRACT_004", "signals.metrics.MICROSOFTAGENTSAI", "metrics", "MICROSOFTAGENTSAI", "OTEL_DOTNET_AUTO_METRICS_MICROSOFTAGENTSAI_INSTRUMENTATION_ENABLED"),
        ("QYL_NATIVE_CONTRACT_005", "signals.traces.MICROSOFTAGENTSAIWORKFLOWS", "traces", "MICROSOFTAGENTSAIWORKFLOWS", "OTEL_DOTNET_AUTO_TRACES_MICROSOFTAGENTSAIWORKFLOWS_INSTRUMENTATION_ENABLED"),
        ("QYL_NATIVE_CONTRACT_006", "signals.traces.MCP", "traces", "MCP", "OTEL_DOTNET_AUTO_TRACES_MCP_INSTRUMENTATION_ENABLED"),
    ];

    // Every exact version a contract string names is the Directory.Packages.props pin, read here
    // rather than typed, so a pin bump cannot leave a stale "verified against" behind.
    private static IReadOnlyDictionary<string, string> QylNative8SupportedPaths => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["signals.traces.MICROSOFTEXTENSIONSAI"] = $"Microsoft.Extensions.AI =={Repo.Pin("Microsoft.Extensions.AI")}; ChatClientBuilder.UseOpenTelemetry() wrapper only",
        ["signals.metrics.MICROSOFTEXTENSIONSAI"] = $"Microsoft.Extensions.AI =={Repo.Pin("Microsoft.Extensions.AI")}; ChatClientBuilder.UseOpenTelemetry() wrapper only",
        ["signals.traces.MICROSOFTAGENTSAI"] = $"Microsoft.Agents.AI =={Repo.Pin("Microsoft.Agents.AI")}; AIAgentBuilder.UseOpenTelemetry() wrapper only",
        ["signals.metrics.MICROSOFTAGENTSAI"] = $"Microsoft.Agents.AI =={Repo.Pin("Microsoft.Agents.AI")}; AIAgentBuilder.UseOpenTelemetry() wrapper only",
        ["signals.traces.MICROSOFTAGENTSAIWORKFLOWS"] = $"Microsoft.Agents.AI.Workflows =={Repo.Pin("Microsoft.Agents.AI.Workflows")}; WorkflowBuilder.WithOpenTelemetry() path only",
        ["signals.traces.MCP"] = $"ModelContextProtocol =={Repo.Pin("ModelContextProtocol")}; automatic official client/server ActivitySource path only",
    };

    public static IReadOnlyDictionary<string, string> QylSupportedVersionOverrides => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["signals.traces.ELASTICSEARCH"] = $"Elastic.Clients.Elasticsearch >=8.0.0; verified against {Repo.Pin("Elastic.Clients.Elasticsearch")}; spans come from the Elastic.Transport ActivitySource the client enriches",
        ["signals.traces.ELASTICTRANSPORT"] = $"Elastic.Transport >=1.0.0; verified against {Repo.Pin("Elastic.Transport")}; native ActivitySource \"Elastic.Transport\"",
        ["signals.metrics.NSERVICEBUS"] = $"NServiceBus >=8.0.0; verified against {Repo.Pin("NServiceBus")}; the native meters NServiceBus.Core and NServiceBus.Core.Pipeline.Incoming, registered by the consumer through OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES",
        ["signals.traces.NSERVICEBUS"] = $"NServiceBus >=8.0.0; verified against {Repo.Pin("NServiceBus")}; native ActivitySource \"NServiceBus.Core\" (enabled by default from v10)",
        ["signals.traces.MONGODB"] = $"MongoDB.Driver >=3.7.0; verified against {Repo.Pin("MongoDB.Driver")}; native ActivitySource \"MongoDB.Driver\"",
        ["signals.traces.RABBITMQ"] = $"RabbitMQ.Client >=7.0.0; verified against {Repo.Pin("RabbitMQ.Client")}; native ActivitySources \"RabbitMQ.Client.Publisher\" and \"RabbitMQ.Client.Subscriber\"",
        ["signals.traces.MASSTRANSIT"] = $"MassTransit >=8.0.0 (Apache-2.0); verified against {Repo.Pin("MassTransit.RabbitMQ")}; native ActivitySource \"MassTransit\"",
        ["signals.traces.QUARTZ"] = $"Quartz >=4.0.0; verified against {Repo.Pin("Quartz")}; native ActivitySource \"Quartz\" (3.x used DiagnosticListener and has none)",
        ["signals.metrics.QUARTZ"] = $"Quartz >=4.0.0 on .NET 10+; verified against {Repo.Pin("Quartz")}; native Meter \"Quartz\"",
        ["signals.traces.WCFCORE"] = $"CoreWCF.Primitives =={Repo.Pin("CoreWCF.Http")}; CoreWCF.Http =={Repo.Pin("CoreWCF.Http")}; server ActivitySource path only",
    };

    private static readonly HashSet<string> ManagedNativeAotBoundarySignalKeys = new(StringComparer.Ordinal)
    {
        "signals.traces.KAFKA",
        "signals.traces.MONGODB",
        "signals.traces.MYSQLDATA",
        "signals.traces.NSERVICEBUS",
        "signals.traces.QUARTZ",
        "signals.metrics.QUARTZ",
        "signals.traces.WCFCLIENT",
        "signals.traces.WCFCORE",
        "signals.traces.MICROSOFTEXTENSIONSAI",
        "signals.metrics.MICROSOFTEXTENSIONSAI",
        "signals.traces.MICROSOFTAGENTSAI",
        "signals.metrics.MICROSOFTAGENTSAI",
        "signals.traces.MICROSOFTAGENTSAIWORKFLOWS",
    };

    private static readonly string[] CommonItemProperties =
    [
        "kind", "key", "contract_origin", "status", "index", "contract_item_id", "upstream_sources", "lane", "qyl_status",
        "call_site_visibility", "payload_access", "evidence_level", "primary_owner", "evidence", "conformance_signals", "unsupported_reason",
    ];

    public static readonly HashSet<string> SignalItemProperties = new([.. CommonItemProperties,
        "signal", "instrumentation_id", "environment_toggle", "environment_toggle_origin", "libraries", "supported_versions",
        "instrumentation_types", "instrumentation_type_operator", "not_supported_on", "notes", "documentation"], StringComparer.Ordinal);

    public static readonly HashSet<string> ControlItemProperties = new([.. CommonItemProperties,
        "environment_variable", "scope", "description", "default_value", "signal", "overrides", "is_pattern", "placeholder"], StringComparer.Ordinal);

    public static readonly HashSet<string> OptionItemProperties = new([.. CommonItemProperties,
        "environment_variable", "target_signal", "target_instrumentation_id", "description", "attribute_names", "default_value",
        "default_behavior", "value_format", "value_type", "not_supported_on", "notes"], StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedItemPropertiesByKind = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        [SignalKind] = SignalItemProperties,
        [ControlKind] = ControlItemProperties,
        [OptionKind] = OptionItemProperties,
    };

    public static Item Load()
    {
        var upstream = LoadYamlFile(UpstreamContractPath);
        var qylNative = LoadYamlFile(QylNativeContractPath);
        var ownership = LoadYamlFile(OwnershipPath);
        return Resolve(upstream, qylNative, ownership);
    }

    private static Item LoadYamlFile(AbsolutePath path)
    {
        Gate.Require(path.FileExists(), $"missing contract source: {Repo.Relative(path)}");
        return Yaml.Load(path) as Item ?? throw Gate.Fail($"YAML root must be an object: {Repo.Relative(path)}");
    }

    private static Item Resolve(Item upstream, Item qylNative, Item ownership)
    {
        var upstreamItems = upstream.Get("contract_items") as List<object?> ?? throw Gate.Fail("upstream contract must contain contract_items[]");
        var qylNativeItems = qylNative.Get("contract_items") as List<object?> ?? throw Gate.Fail("qyl-native contract must contain contract_items[]");
        var ownershipItems = ownership.Get("ownership_items") as List<object?> ?? throw Gate.Fail("qyl ownership overlay must contain ownership_items[]");

        VerifySourceContractSequences(upstreamItems.Cast<Item>().ToList(), qylNativeItems.Cast<Item>().ToList());

        var overlayById = new OrderedDictionary<string, Item>(StringComparer.Ordinal);
        foreach (var rawOverlay in ownershipItems)
        {
            if (rawOverlay is not Item overlay)
                throw Gate.Fail($"ownership item must be an object: {ContractValue.Render(rawOverlay)}");
            var contractItemId = overlay.GetText("contract_item_id");
            Gate.Require(contractItemId.Length > 0, "ownership item missing contract_item_id");
            Gate.Require(!overlayById.ContainsKey(contractItemId), $"duplicate ownership item: {contractItemId}");
            overlayById[contractItemId] = new Item(overlay, StringComparer.Ordinal);
        }

        string[] overlayFields =
        [
            "lane", "qyl_status", "call_site_visibility", "payload_access", "evidence_level", "primary_owner", "evidence",
            "conformance_signals", "unsupported_reason", "supported_versions",
        ];
        var resolvedItems = new List<object?>();
        foreach (var rawItem in upstreamItems.OrderBy(static candidate => ContractValue.Int(((Item)candidate!).Get("index"))))
        {
            if (rawItem is not Item raw)
                throw Gate.Fail($"upstream contract item must be an object: {ContractValue.Render(rawItem)}");
            var item = new Item(raw, StringComparer.Ordinal) { ["contract_origin"] = UpstreamOrigin };
            var contractItemId = item.GetText("contract_item_id");
            if (!overlayById.Remove(contractItemId, out var overlay))
                throw Gate.Fail($"qyl ownership overlay missing contract item: {contractItemId}");
            Gate.Require(overlay.GetText("key") == item.GetText("key"), $"qyl ownership overlay key mismatch for {contractItemId}");
            foreach (var field in overlayFields)
            {
                if (overlay.TryGetValue(field, out var value))
                    item[field] = value;
            }

            resolvedItems.Add(item);
        }

        Gate.Require(overlayById.Count == 0, () => $"qyl ownership overlay contains unknown contract items: {ContractValue.RenderSorted(overlayById.Keys)}");

        foreach (var rawItem in qylNativeItems.OrderBy(static candidate => ContractValue.Int(((Item)candidate!).Get("index"))))
        {
            if (rawItem is not Item raw)
                throw Gate.Fail($"qyl-native contract item must be an object: {ContractValue.Render(rawItem)}");
            resolvedItems.Add(new Item(raw, StringComparer.Ordinal) { ["contract_origin"] = QylNativeOrigin });
        }

        return new Item(StringComparer.Ordinal)
        {
            ["schema_id"] = "qyl-aot-autoinstrumentation-resolved-contract",
            ["schema_version"] = "2.0.0",
            ["generated_at"] = qylNative.TryGetValue("generated_at", out var generatedAt) ? ContractValue.Text(generatedAt) : "2026-07-19",
            ["sources"] = new Item(StringComparer.Ordinal)
            {
                [UpstreamOrigin] = upstream.TryGetValue("source", out var upstreamSource) ? upstreamSource : new Item(),
                [QylNativeOrigin] = qylNative.TryGetValue("source", out var nativeSource) ? nativeSource : new Item(),
            },
            ["generated_from"] = new Item(StringComparer.Ordinal)
            {
                ["upstream_contract"] = Repo.Relative(UpstreamContractPath),
                ["qyl_native_contract"] = Repo.Relative(QylNativeContractPath),
                ["qyl_ownership"] = Repo.Relative(OwnershipPath),
            },
            ["contract_items"] = resolvedItems,
        };
    }

    private static void VerifySourceContractSequences(IReadOnlyList<Item> upstreamItems, IReadOnlyList<Item> qylNativeItems)
    {
        var upstreamLast = UpstreamContractItemCount;
        Gate.Require(upstreamItems.Count == upstreamLast, $"upstream contract must remain exactly {upstreamLast} items: {upstreamItems.Count}");
        var upstreamIndexes = upstreamItems.Select(static item => ContractValue.Int(item.Get("index"))).ToArray();
        Gate.Require(upstreamIndexes.SequenceEqual(Enumerable.Range(1, upstreamLast).Select(static index => (long)index)),
            () => $"upstream contract indexes must remain contiguous 1..{upstreamLast}: [{string.Join(", ", upstreamIndexes)}]");
        var upstreamIds = upstreamItems.Select(static item => item.GetText("contract_item_id")).ToArray();
        Gate.Require(upstreamIds.SequenceEqual(Enumerable.Range(1, upstreamLast).Select(static index => $"OTEL_DOTNET_AUTO_CONTRACT_{index:000}")),
            "upstream contract_item_id sequence mismatch");

        Gate.Require(qylNativeItems.Count == QylNativeContractItemCount,
            $"qyl-native 8.0 contract must contain exactly {QylNativeContractItemCount} scoped signal items: {qylNativeItems.Count}");
        var nativeIndexes = qylNativeItems.Select(static item => ContractValue.Int(item.Get("index"))).ToArray();
        var expectedNativeRange = Enumerable.Range(upstreamLast + 1, QylNativeContractItemCount).Select(static index => (long)index).ToArray();
        Gate.Require(nativeIndexes.SequenceEqual(expectedNativeRange),
            () => $"qyl-native contract indexes must be contiguous {expectedNativeRange[0]}..{expectedNativeRange[^1]}: [{string.Join(", ", nativeIndexes)}]");
        var nativeIds = qylNativeItems.Select(static item => item.GetText("contract_item_id")).ToArray();
        Gate.Require(nativeIds.SequenceEqual(Enumerable.Range(1, QylNativeContractItemCount).Select(static index => $"QYL_NATIVE_CONTRACT_{index:000}")),
            "qyl-native contract_item_id sequence mismatch");

        var actualNativeContract = qylNativeItems
            .Select(static item => (item.GetText("contract_item_id"), item.GetText("key"), item.GetText("signal"), item.GetText("instrumentation_id"), item.GetText("environment_toggle")))
            .ToArray();
        Gate.Require(actualNativeContract.SequenceEqual(QylNative8SignalContract),
            () => $"qyl-native 8.0 signal scope mismatch: expected=[{string.Join(", ", QylNative8SignalContract)}] actual=[{string.Join(", ", actualNativeContract)}]");

        var expectedPaths = QylNative8SupportedPaths;
        var actualPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in qylNativeItems)
            actualPaths[item.GetText("key")] = item.GetText("supported_versions");
        Gate.Require(actualPaths.Count == expectedPaths.Count && actualPaths.All(pair => expectedPaths.TryGetValue(pair.Key, out var expected) && expected == pair.Value),
            () => "qyl-native 8.0 supported path/version mismatch: "
                  + $"expected={{{string.Join(", ", expectedPaths.Select(static pair => $"{pair.Key}: {pair.Value}"))}}} "
                  + $"actual={{{string.Join(", ", actualPaths.Select(static pair => $"{pair.Key}: {pair.Value}"))}}}");
    }

    public static List<Item> ContractItems(Item contract) => ((List<object?>)contract["contract_items"]!).Cast<Item>().ToList();

    private static List<Item> SignalItems(Item contract) => ContractItems(contract).Where(static item => item.GetText("kind") == SignalKind).ToList();

    public static List<Item> ImplementedSignalItems(Item contract) =>
        SignalItems(contract).Where(static item => item.Get("qyl_status") is "implemented").ToList();

    public static List<Item> SourceInterceptorSignalItems(Item contract) =>
        ImplementedSignalItems(contract).Where(static item => item.Get("lane") is "source_interceptor").ToList();

    public static List<Item> UnsupportedSignalItems(Item contract) =>
        SignalItems(contract).Where(static item => item.Get("qyl_status") is "unsupported_nativeaot").ToList();

    public static IReadOnlyList<string> Evidence(Item item) =>
        item.Get("evidence") is List<object?> entries ? entries.OfType<string>().ToArray() : [];

    public static OrderedDictionary<string, long> ContractCounts(Item contract)
    {
        var items = ContractItems(contract);
        var signals = SignalItems(contract);
        return new OrderedDictionary<string, long>(StringComparer.Ordinal)
        {
            ["upstream_contract_items"] = items.Count(static item => item.Get("contract_origin") is UpstreamOrigin),
            ["qyl_native_contract_items"] = items.Count(static item => item.Get("contract_origin") is QylNativeOrigin),
            ["signal_specific_instrumentation_promises"] = signals.Count,
            ["global_environment_controls"] = items.Count(static item => item.GetText("kind") == ControlKind),
            ["instrumentation_options"] = items.Count(static item => item.GetText("kind") == OptionKind),
            ["total_contract_items"] = items.Count,
            ["traces_signal_specific_promises"] = signals.Count(static item => item.Get("signal") is "traces"),
            ["metrics_signal_specific_promises"] = signals.Count(static item => item.Get("signal") is "metrics"),
            ["logs_signal_specific_promises"] = signals.Count(static item => item.Get("signal") is "logs"),
            ["unique_instrumentation_ids"] = signals.Select(static item => ContractValue.Text(item.Get("instrumentation_id"))).Distinct(StringComparer.Ordinal).Count(),
        };
    }

    public static void VerifyContractModel(Item contract)
    {
        var items = ContractItems(contract);
        Gate.Require(items.Count == TotalContractItemCount, $"wrong contract item count: {items.Count}");

        var indexes = items.Select(static item => ContractValue.Int(item.Get("index"))).ToArray();
        Gate.Require(indexes.SequenceEqual(Enumerable.Range(1, TotalContractItemCount).Select(static index => (long)index)),
            () => $"resolved contract indexes must be contiguous 1..{TotalContractItemCount}: [{string.Join(", ", indexes)}]");

        var expectedIds = Enumerable.Range(1, UpstreamContractItemCount).Select(static index => $"OTEL_DOTNET_AUTO_CONTRACT_{index:000}")
            .Concat(Enumerable.Range(1, QylNativeContractItemCount).Select(static index => $"QYL_NATIVE_CONTRACT_{index:000}"));
        Gate.Require(items.Select(static item => item.GetText("contract_item_id")).SequenceEqual(expectedIds), "contract_item_id sequence mismatch");

        var counts = ContractCounts(contract);
        var expectedCounts = new OrderedDictionary<string, long>(StringComparer.Ordinal)
        {
            ["upstream_contract_items"] = UpstreamContractItemCount,
            ["qyl_native_contract_items"] = QylNativeContractItemCount,
            ["signal_specific_instrumentation_promises"] = 44,
            ["global_environment_controls"] = 7,
            ["instrumentation_options"] = 19,
            ["total_contract_items"] = TotalContractItemCount,
            ["traces_signal_specific_promises"] = 30,
            ["metrics_signal_specific_promises"] = 11,
            ["logs_signal_specific_promises"] = 3,
            ["unique_instrumentation_ids"] = 35,
        };
        Gate.Require(counts.Count == expectedCounts.Count && counts.Zip(expectedCounts).All(static pair =>
                string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal) && pair.First.Value == pair.Second.Value),
            () => $"contract count mismatch: expected={{{string.Join(", ", expectedCounts.Select(static pair => $"{pair.Key}: {pair.Value}"))}}} actual={{{string.Join(", ", counts.Select(static pair => $"{pair.Key}: {pair.Value}"))}}}");

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            VerifyContractItem(item);
            var key = item.GetText("key");
            Gate.Require(seenKeys.Add(key), $"duplicate contract key: {key}");
        }

        VerifySourceContractSequences(
            items.Where(static item => item.Get("contract_origin") is UpstreamOrigin).ToList(),
            items.Where(static item => item.Get("contract_origin") is QylNativeOrigin).ToList());

        var itemsByKey = items.ToDictionary(static item => item.GetText("key"), StringComparer.Ordinal);
        foreach (var (key, expected) in QylSupportedVersionOverrides)
        {
            var actual = itemsByKey[key].GetText("supported_versions");
            Gate.Require(actual == expected, $"qyl supported-version override mismatch for {key}: expected={ContractValue.Render(expected)} actual={ContractValue.Render(actual)}");
        }

        VerifyManagedNativeAotBoundarySemantics(contract);
        VerifyConformanceSignalSemantics(contract);
    }

    private static void VerifyContractItem(Item item)
    {
        var kind = item.GetText("kind");
        var key = item.GetText("key");
        if (!AllowedItemPropertiesByKind.TryGetValue(kind, out var allowed))
            throw Gate.Fail($"unknown contract item kind for {key}: {kind}");
        var unexpected = item.Keys.Where(property => !allowed.Contains(property)).Sorted().ToArray();
        Gate.Require(unexpected.Length == 0, () => $"unexpected properties for {key}: {ContractValue.RenderSorted(unexpected)}");

        foreach (var field in new[] { "lane", "qyl_status", "call_site_visibility", "payload_access", "evidence_level", "primary_owner", "evidence" })
            Gate.Require(item.ContainsKey(field), $"missing qyl ownership field {field}: {key}");

        var lane = item.GetText("lane");
        var status = item.GetText("qyl_status");
        var visibility = item.GetText("call_site_visibility");
        var payload = item.GetText("payload_access");
        var evidenceLevel = item.GetText("evidence_level");
        var rawEvidence = item.Get("evidence");
        var upstreamSources = item.Get("upstream_sources");
        var contractOrigin = item.GetText("contract_origin");

        Gate.Require(contractOrigin is UpstreamOrigin or QylNativeOrigin, $"invalid contract_origin for {key}: {contractOrigin}");
        Gate.Require(Lanes.Contains(lane), $"invalid lane for {key}: {lane}");
        Gate.Require(Statuses.Contains(status), $"invalid qyl_status for {key}: {status}");
        Gate.Require(CallSiteVisibilities.Contains(visibility), $"invalid call_site_visibility for {key}: {visibility}");
        Gate.Require(PayloadAccessValues.Contains(payload), $"invalid payload_access for {key}: {payload}");
        Gate.Require(EvidenceLevels.Contains(evidenceLevel), $"invalid evidence_level for {key}: {evidenceLevel}");
        Gate.Require(rawEvidence is List<object?> list && list.All(static entry => entry is string), $"evidence must be a string array for {key}");
        var evidence = ((List<object?>)rawEvidence!).Cast<string>().ToArray();
        Gate.Require(upstreamSources is List<object?> { Count: > 0 }, $"upstream_sources must be a non-empty array for {key}");
        foreach (var rawSource in (List<object?>)upstreamSources!)
        {
            if (rawSource is not Item source)
                throw Gate.Fail($"upstream source must be an object for {key}: {ContractValue.Render(rawSource)}");
            Gate.Require(source.Count == 2 && source.ContainsKey("label") && source.ContainsKey("url"),
                $"upstream source must contain only label and url for {key}: {ContractValue.Render(source)}");
            var label = source.GetText("label");
            var url = source.GetText("url");
            Gate.Require(label.Length > 0, $"upstream source label must be non-empty for {key}");
            Gate.Require(url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal),
                $"upstream source url must be HTTP(S) for {key}: {url}");
            Gate.Require(!url.Contains("filecite", StringComparison.Ordinal) && !url.Contains("turn", StringComparison.Ordinal),
                $"upstream source url must be reusable outside a chat session for {key}: {url}");
        }

        Gate.Require(!(visibility == "library_internal" && lane == "source_interceptor"), $"library_internal item cannot use source_interceptor lane: {key}");
        if (lane == "native_source")
        {
            Gate.Require(payload == "typed_public", $"native_source item must use typed_public payload access: {key}");
            if (status == "implemented")
            {
                foreach (var token in new[] { "src/Qyl.Telemetry.Hosting/QylTelemetrySources.cs", "src/Qyl.Telemetry.Hosting/QylNativeSpanProcessor.cs" })
                {
                    Gate.Require(evidence.Contains(token, StringComparer.Ordinal),
                        $"native_source item must carry the source table and processor as evidence for {key}: missing {token}");
                }

                Gate.Require(evidence.Any(ContractEvidence.IsDemoLane), $"native_source item must carry real demo lane evidence: {key}");
            }
        }

        Gate.Require(!(lane == "runtime_public_telemetry" && payload is not ("typed_public" or "aot_reflection")),
            $"runtime_public_telemetry item must use typed_public or aot_reflection payload access: {key}");
        Gate.Require(!(payload == "aot_reflection" && evidenceLevel != "verified_nativeaot"), $"aot_reflection item must carry verified_nativeaot evidence: {key}");
        Gate.Require(!(payload == "reflection_required" && status is not ("research_required" or "unsupported_nativeaot")),
            $"reflection_required item must be research_required or unsupported_nativeaot: {key}");
        Gate.Require(!(key == "signals.traces.SQLCLIENT" && lane != "source_interceptor"), "SqlClient trace contract must remain interceptor-primary");

        // An unsupported row must state WHY it is unsupported; a supported row must not carry a
        // leftover reason. Reasons are adversarially-verified facts, not prose.
        var unsupportedReason = item.GetText("unsupported_reason");
        Gate.Require(!(status == "unsupported_nativeaot" && unsupportedReason.Length == 0), $"unsupported_nativeaot item must carry unsupported_reason: {key}");
        Gate.Require(!(status != "unsupported_nativeaot" && unsupportedReason.Length > 0), $"non-unsupported item must not carry unsupported_reason: {key}");

        if (status == "implemented")
        {
            Gate.Require(evidenceLevel != "none", $"implemented item must carry evidence_level != none: {key}");
            Gate.Require(evidence.Length > 0, $"implemented item must carry evidence: {key}");
            var missing = evidence.Where(static entry => !File.Exists(Repo.Root / entry) && !Directory.Exists(Repo.Root / entry)).ToArray();
            Gate.Require(missing.Length == 0, () => $"implemented item evidence path does not exist for {key}: {ContractValue.Render(missing.ToList())}");
            if (lane == "source_interceptor")
            {
                foreach (var token in new[] { ContractEvidence.GeneratorSource, ContractEvidence.SourceInterceptorConsumer })
                {
                    Gate.Require(evidence.Contains(token, StringComparer.Ordinal),
                        $"source_interceptor item must carry generator and consumer proof for {key}: missing {token}");
                }

                Gate.Require(evidenceLevel == "compile_binding_only" || evidence.Any(ContractEvidence.IsDemoLane),
                    $"source_interceptor item must carry real demo lane evidence: {key}");
            }

            Gate.Require(evidenceLevel != "compile_binding_only", $"implemented signals require runtime evidence: {key}");

            if (lane == "runtime_public_telemetry")
            {
                string[] proofTokens =
                [
                    "src/Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners",
                    "DiagnosticListener",
                    "src/Qyl.Telemetry.AutoInstrumentation/QylMetricMeters.cs",
                    "ILogger",
                    "ActivitySource",
                    "Meter",
                ];
                Gate.Require(evidence.Any(entry => proofTokens.Any(token => entry.Contains(token, StringComparison.Ordinal) || token == entry)),
                    $"runtime_public_telemetry item must carry typed public payload or meter/activity/log proof: {key}");
            }
        }

        Gate.Require(!(status == "control_bound" && lane != "environment_control"), $"control_bound item must use environment_control lane: {key}");
        Gate.Require(!(status == "option_bound" && lane != "instrumentation_option"), $"option_bound item must use instrumentation_option lane: {key}");
    }

    private static void VerifyManagedNativeAotBoundarySemantics(Item contract)
    {
        var managedKeys = SignalItems(contract)
            .Where(static item => item.Get("qyl_status") is "implemented" && item.Get("evidence_level") is "verified_managed")
            .Select(static item => item.GetText("key"))
            .ToHashSet(StringComparer.Ordinal);
        Gate.Require(managedKeys.SetEquals(ManagedNativeAotBoundarySignalKeys),
            () => "verified_managed implemented signals must be an explicit NativeAOT boundary set: "
                  + $"missing_boundary={ContractValue.RenderSorted(managedKeys.Except(ManagedNativeAotBoundarySignalKeys))} "
                  + $"stale_boundary={ContractValue.RenderSorted(ManagedNativeAotBoundarySignalKeys.Except(managedKeys))}");
    }

    private static readonly string[] ConformanceSignalShape = ["kind", "name", "required_attributes", "recommended_attributes", "opt_in_attributes"];

    private static void VerifyConformanceSignalSemantics(Item contract)
    {
        var signalsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in SignalItems(contract))
        {
            var raw = item.TryGetValue("conformance_signals", out var value) ? value : new List<object?>();
            raw ??= new List<object?>();
            if (raw is not List<object?> conformanceSignals)
                throw Gate.Fail($"conformance_signals must be an array: {item.GetText("key")}");
            var verifiedNativeAot = item.Get("qyl_status") is "implemented" && item.Get("evidence_level") is "verified_nativeaot";
            Gate.Require(!(verifiedNativeAot && conformanceSignals.Count == 0), $"verified NativeAOT signal must declare conformance_signals: {item.GetText("key")}");
            Gate.Require(!(conformanceSignals.Count > 0 && item.Get("qyl_status") is not "implemented"), $"conformance signal declared for non-implemented item: {item.GetText("key")}");
            Gate.Require(!(conformanceSignals.Count > 0 && item.Get("evidence_level") is not "verified_nativeaot"), $"conformance signal declared for non-NativeAOT evidence item: {item.GetText("key")}");
            foreach (var rawSignal in conformanceSignals)
            {
                if (rawSignal is not Item signal)
                    throw Gate.Fail($"conformance signal must be an object: {item.GetText("key")}");
                Gate.Require(signal.Count == ConformanceSignalShape.Length && ConformanceSignalShape.All(signal.ContainsKey),
                    () => $"conformance signal has unexpected shape: {item.GetText("key")} {ContractValue.Render(signal)}");
                var kind = ContractValue.Text(signal["kind"]);
                var name = ContractValue.Text(signal["name"]);
                Gate.Require(ConformanceKinds.Contains(kind), $"invalid conformance signal kind for {name}: {kind}");
                foreach (var attributeField in new[] { "required_attributes", "recommended_attributes", "opt_in_attributes" })
                {
                    Gate.Require(signal[attributeField] is List<object?> attributes && attributes.All(static attribute => attribute is string),
                        $"{attributeField} must be a string array for conformance signal {name}");
                    var values = ((List<object?>)signal[attributeField]!).Cast<string>().ToArray();
                    Gate.Require(values.Length == values.Distinct(StringComparer.Ordinal).Count(), $"duplicate {attributeField} entries for conformance signal {name}");
                }

                Gate.Require(!Strings(signal["required_attributes"]).Contains("error.type", StringComparer.Ordinal), $"error.type must not be required in conformance signal {name}");
                var signature = string.Join("\u0001", kind, string.Join("\u0002", Strings(signal["required_attributes"])),
                    string.Join("\u0002", Strings(signal["recommended_attributes"])), string.Join("\u0002", Strings(signal["opt_in_attributes"])));
                if (signalsByName.TryGetValue(name, out var previous) && previous != signature)
                    throw Gate.Fail($"conformance signal name has conflicting declarations: {name}");
                signalsByName[name] = signature;
            }
        }

        var assignedNames = new HashSet<string>(StringComparer.Ordinal);
        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var serviceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in ConformanceProfiles)
        {
            Gate.Require(profileIds.Add(profile.ProfileId), $"duplicate conformance profile_id: {profile.ProfileId}");
            Gate.Require(serviceNames.Add(profile.ServiceName), $"duplicate conformance service_name: {profile.ServiceName}");
            Gate.Require(profile is not { ProfileId: "qyl-aot-unsupported-nativeaot", SignalNames.Length: > 0 }, "unsupported NativeAOT conformance profile must expect no signals");
            Gate.Require(profile is not { ProfileId: not "qyl-aot-unsupported-nativeaot", SignalNames.Length: 0 }, $"conformance profile must expect at least one signal: {profile.ProfileId}");
            foreach (var name in profile.SignalNames)
                Gate.Require(assignedNames.Add(name), $"conformance signal assigned to multiple profiles: {name}");
        }

        Gate.Require(profileIds.SetEquals(RequiredConformanceProfileIds),
            () => "conformance plan must preserve multi-profile fixture coverage: "
                  + $"missing={ContractValue.RenderSorted(RequiredConformanceProfileIds.Except(profileIds))} stale={ContractValue.RenderSorted(profileIds.Except(RequiredConformanceProfileIds))}");

        var names = signalsByName.Keys.ToHashSet(StringComparer.Ordinal);
        var missingProfile = names.Except(assignedNames).ToArray();
        var staleProfile = assignedNames.Except(names).ToArray();
        Gate.Require(missingProfile.Length == 0 && staleProfile.Length == 0,
            () => $"conformance signal/profile mismatch: missing_profile={ContractValue.RenderSorted(missingProfile)} stale_profile={ContractValue.RenderSorted(staleProfile)}");

        var sql = ConformanceSignalsForPlan(contract).FirstOrDefault(static signal => ContractValue.Text(signal["name"]) == "sqlclient.command")
                  ?? throw Gate.Fail("sqlclient.command conformance signal missing");
        Gate.Require(Strings(sql["required_attributes"]).ToHashSet(StringComparer.Ordinal).SetEquals(["db.system.name", "db.operation.name", "db.query.summary", "qyl.instrumentation.domain"]),
            "sqlclient.command required attributes must come from semantic ownership, not error snapshots");
        Gate.Require(Strings(sql["recommended_attributes"]).ToHashSet(StringComparer.Ordinal).SetEquals(["error.type"]), "sqlclient.command error.type must be recommended");
        Gate.Require(Strings(sql["opt_in_attributes"]).ToHashSet(StringComparer.Ordinal).SetEquals(["db.query.text"]), "sqlclient.command db.query.text must be opt-in");
    }

    public static List<Item> ConformanceSignalsForPlan(Item contract)
    {
        var signals = new List<Item>();
        foreach (var item in SignalItems(contract))
        {
            if (item.Get("conformance_signals") is List<object?> declared)
                signals.AddRange(declared.Cast<Item>());
        }

        // A stable sort: equal names keep their declaration order.
        return signals.OrderBy(static signal => ContractValue.Text(signal["name"]), StringComparer.Ordinal).ToList();
    }

    public static string[] Strings(object? value) => value is List<object?> list ? list.Select(ContractValue.Text).ToArray() : [];
}

/// <summary>What the contract may cite as evidence, now that the gates are Fallout targets.</summary>
static class ContractEvidence
{
    public const string GeneratorSource = "src/Qyl.Telemetry.AutoInstrumentation.SourceGenerators/QylAutoInstrumentationGenerator.cs";
    public const string SourceInterceptorConsumer = "eng/build/ConsumerBehavior/SourceInterceptorConsumer.cs";

    /// <summary>The gates other than demo lanes that execute a published consumer at runtime.</summary>
    private static readonly HashSet<string> RuntimeGates = new(StringComparer.Ordinal)
    {
        "eng/build/Smoke/SmokeGate.cs",
        "eng/build/ConsumerBehavior/NativeAotConsumer.cs",
        "eng/build/WebApiAotDemo/WebApiAotDemoGate.cs",
    };

    public static bool IsDemoLane(string entry) => DemoLanes.ByEvidencePath(entry) is not null;

    /// <summary>Any demo lane file, registered or not, or one of the runtime gates: a
    /// compile-binding-only row may cite none of them.</summary>
    public static bool IsRuntimeVerification(string entry) =>
        entry.StartsWith("tests/Qyl.Telemetry.Integration.Tests/Lanes/", StringComparison.Ordinal) || RuntimeGates.Contains(entry);
}
