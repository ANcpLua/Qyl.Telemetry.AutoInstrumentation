using System.Text;
using System.Text.RegularExpressions;
using Item = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace Qyl.Build.Contracts;

/// <summary>
/// The contract-invariants gate. Every check holds under exactly one authority class:
/// <list type="bullet">
/// <item>coherence: it cross-checks two owned artifacts (contract YAML, generated outputs, generator
/// source, options source, the demo lane registry) that no other gate compares against each other;</item>
/// <item>external contract: it encodes a cited external contract — the Roslyn interceptors feature,
/// well-known .NET meter names, upstream OTEL environment variables, or the generated semconv surface;</item>
/// <item>philosophy guard: it encodes a negative architectural guarantee (no reflection, profiler or
/// IL-rewrite mechanisms, no inline telemetry in the generator, preserved exception semantics, bounded
/// span names, centralized sensitive-value capture) that snapshots cannot express.</item>
/// </list>
/// Positive substring pins over internal member names are intentionally absent: emitted-source shape is
/// byte-pinned by GeneratorSnapshots, runtime claims are owned by the demo lanes, and the public API
/// surface by ApiBaseline.
/// </summary>
static partial class ContractInvariants
{
    private static AbsolutePath GeneratorPath => Repo.Src / "Qyl.Telemetry.AutoInstrumentation.SourceGenerators" / "QylAutoInstrumentationGenerator.cs";
    private static AbsolutePath RuntimeSourceRoot => Repo.Src / "Qyl.Telemetry.AutoInstrumentation";
    private static AbsolutePath DeclarationsPath => RuntimeSourceRoot / "GeneratedCode" / "QylDeclarations.cs";
    private static AbsolutePath VerifiedRoot => Repo.Root / "tests" / "Qyl.Telemetry.AutoInstrumentation.SourceGenerators.Snapshots" / "verified";
    public static AbsolutePath GeneratedInterceptorSnapshotPath => VerifiedRoot / "QylAutoInstrumentation.Interceptors.g.verified.cs";
    private static AbsolutePath GeneratedInterceptorManifestArtifactPath => VerifiedRoot / "QylAutoInstrumentation.ContractManifests.verified.jsonl";
    private static AbsolutePath OptionsPath => RuntimeSourceRoot / "QylAutoInstrumentationOptions.cs";
    private static AbsolutePath IdsPath => RuntimeSourceRoot / "QylAutoInstrumentationIds.cs";
    private static AbsolutePath RuntimeProjectPath => RuntimeSourceRoot / "Qyl.Telemetry.AutoInstrumentation.csproj";
    private static AbsolutePath MetricMetersPath => RuntimeSourceRoot / "QylMetricMeters.cs";
    private static AbsolutePath DbClientMetricsPath => RuntimeSourceRoot / "QylDbClientMetrics.cs";
    private static AbsolutePath SensitiveCapturePolicyPath => RuntimeSourceRoot / "Internal" / "QylSensitiveCapturePolicy.cs";

    private static AbsolutePath[] RuntimeEmissionRoots =>
    [
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.SqlClient",
    ];

    private static AbsolutePath[] ProductiveMechanismRoots =>
    [
        Repo.Src / "Qyl.Telemetry.Hosting",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.Hosting",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.SqlClient",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore",
        Repo.Src / "Qyl.Telemetry.AutoInstrumentation.SourceGenerators",
    ];

    // Philosophy guard: no runtime-dispatch instrumentation substrate.
    private static readonly string[] ForbiddenGeneratorRuntimeDispatchTokens =
        ["IOperationInvoker", "IServiceBehavior", "IOperationBehavior", "DispatchOperation", "dispatchOperation.Invoker", "QylCoreWcf"];

    // Philosophy guard: forbidden instrumentation mechanisms across all productive code (no profiler,
    // startup hook, ReJIT, IL rewriting, dynamic plugin loading, or reflection-based dispatch).
    private static readonly string[] ForbiddenMechanismTokens =
    [
        "CORECLR_PROFILER", "DOTNET_STARTUP_HOOKS", "ICorProfiler", "ReJIT", "ILRewrite", "ILRewriter", "Assembly.Load",
        "Assembly.GetTypes", "System.Reflection", "Activator.CreateInstance", "Type.GetType", "GetTypes(", "GetFields(",
        "GetProperties(", "GetMethods(", "GetProperty(", "GetField(", "PropertyInfo", "FieldInfo", "MakeGeneric", "CallSite", "dynamic ",
    ];

    // External contract: https://github.com/dotnet/roslyn/blob/main/docs/features/interceptors.md.
    // Interceptable locations must come from the Roslyn API, never be synthesized.
    private static readonly string[] RequiredRoslynInterceptorContractTokens =
    [
        "node is InvocationExpressionSyntax",
        "GetInterceptorMethod(invocation, cancellationToken) is not null",
        "GetInterceptableLocation(invocation, cancellationToken)",
        "is not { } interceptableLocation",
        "GetInterceptsLocationAttributeSyntax(",
        "file sealed class InterceptsLocationAttribute",
    ];

    private static readonly string[] ForbiddenRoslynInterceptorContractTokens = ["new InterceptableLocation", "InterceptableLocation.Create", "Location.Create"];

    // A Roslyn Location is not an InterceptableLocation. Reporting a diagnostic needs an ordinary source
    // Location; only the qyl generator diagnostics may take one, at most twice.
    private const string DiagnosticLocationToken = "GetLocation()";
    private static readonly string[] DiagnosticLocationOwners = ["QylGeneratorDiagnostics.ShapeNotMatched", "QylGeneratorDiagnostics.NativeTelemetryOptInMissing"];
    private const int DiagnosticLocationMaxUses = 2;

    // Philosophy guard: runtime telemetry emits attribute keys through the generated semconv constants.
    private static readonly Regex[] ForbiddenAttributeEmissionLiteralPatterns =
    [
        new(@"\.SetTag\(\s*""[^""]+"""),
        new(@"\.AddTag\(\s*""[^""]+"""),
        new(@"new\s+(?:global::System\.Collections\.Generic\.)?KeyValuePair<string,\s*object\?>\(\s*""[^""]+"""),
    ];

    // Registry-owned attribute keys, in attribute-key form: lowercase, dot-separated segments. A prefix
    // match on '"qyl.' would also hit the gRPC service name "qyl.LiveProbe", a wire identifier.
    private const string QylVocabularyKeyLiteral = @"""qyl(?:\.[a-z0-9_]+)+""";
    private const string SessionIdKeyLiteral = @"""session\.id""";

    // Writing a key: the four Activity tag/baggage setters with the literal as their first argument, and
    // a const whose value is a bare key (a named constant is still the write, one indirection out).
    private static readonly string[] QylVocabularyWriterPatterns =
    [
        @"(?<![A-Za-z0-9_])(?:Set|Add)(?:Tag|Baggage)\(\s*{key}",
        @"(?<![A-Za-z0-9_])const\s+string\s+[A-Za-z0-9_]+\s*=\s*{key}\s*;",
    ];

    // Philosophy guard: the generator delegates behavior to the runtime assembly.
    private static readonly string[] ForbiddenGeneratorInlineTelemetryTokens = ["QylActivitySource", ".SetTag(", "new global::System.Diagnostics.Activity", "ActivitySource"];

    // Philosophy guard: interceptors preserve caller exception stack semantics.
    private static readonly string[] ForbiddenExceptionRewriteTokens = ["throw exception;", "throw caughtException;", "throw ex;"];

    // Coherence: implemented signals that bind no per-instrumentation toggle. Qyl.Telemetry.Hosting
    // registers the OpenTelemetry ILogger provider under the global logs control alone.
    private static readonly HashSet<string> UndeclaredImplementedSignalKeys = new(StringComparer.Ordinal) { "signals.logs.ILOGGER" };

    // Coherence: the explicit NativeAOT boundary — implemented signals whose evidence is managed-only.
    // Restated here so weakening the model cannot silently drop it.
    private static readonly HashSet<string> ManagedEvidenceNativeAotBoundaryKeys = new(StringComparer.Ordinal)
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

    // External contract: well-known meter names Qyl.Telemetry.Hosting registers explicitly. Values, not
    // C# constant names — declarations are free to rename.
    private static readonly HashSet<string> RequiredRegisteredMeterNameValues = new(StringComparer.Ordinal)
    {
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.HeaderParsing",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Authorization",
        "Microsoft.AspNetCore.Authentication",
        "Microsoft.AspNetCore.Components",
        "Microsoft.AspNetCore.Components.Lifecycle",
        "Microsoft.AspNetCore.Components.Server.Circuits",
        "System.Net.Http",
        "System.Net.NameResolution",
        "System.Runtime",
        "Quartz",
        "Qyl.Telemetry.AutoInstrumentation.Database",
    };

    private static readonly HashSet<string> ForbiddenRegisteredMeterNameValues = new(StringComparer.Ordinal) { "Npgsql", "NServiceBus.Core", "NServiceBus.Core.Pipeline.Incoming" };

    // External contract: the registry rows qyl creates instruments from.
    private static readonly HashSet<string> QylOwnedMetricDefinitions = new(StringComparer.Ordinal) { "DbMetricDefinitions.DbClientOperationDuration" };

    private const string MetricsAdditionalSourcesVariable = "OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES";

    // Philosophy guard: sensitive raw values may only be written through the capture policy / owning
    // semantics helper, behind the redaction/opt-in controls.
    private static readonly Regex[] SensitiveRawSetTagPatterns =
    [
        new(@"SetTag\(\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*UrlAttributes\.Query\b"),
        new(@"SetTag\(\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*UrlAttributes\.Full\b"),
        new(@"SetTag\(\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*DbAttributes\.QueryText\b"),
        new(@"SetTag\(\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*GraphqlAttributes\.Document\b"),
    ];

    private static readonly HashSet<string> SensitiveRawSetTagAllowedPaths = new(StringComparer.Ordinal) { "src/Qyl.Telemetry.AutoInstrumentation/Internal/QylSensitiveCapturePolicy.cs" };

    private static readonly Regex[] SensitiveSemanticWriterPatterns =
    [
        new(@"SemanticTagWriter\.Set\(activity,\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*UrlAttributes\.Full\b"),
        new(@"SemanticTagWriter\.Set\(activity,\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*UrlAttributes\.Query\b"),
        new(@"SemanticTagWriter\.Set\(activity,\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*GraphqlAttributes\.Document\b"),
    ];

    private static readonly Regex DbQueryTextWritePattern = new(@"SemanticTagWriter\.Set\(activity,\s*(?:global::)?(?:[A-Za-z0-9_]+\.)*DbAttributes\.QueryText\b");

    private static readonly HashSet<string> UrlFormatAllowedPaths = new(StringComparer.Ordinal)
    {
        "src/Qyl.Telemetry.AutoInstrumentation/Internal/QylCaptureHelpers.cs",
        "src/Qyl.Telemetry.AutoInstrumentation/Internal/QylSensitiveCapturePolicy.cs",
    };

    private static readonly HashSet<string> DbQueryTextAllowedPaths = new(StringComparer.Ordinal)
    {
        "src/Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore/EntityFrameworkCore/EntityFrameworkCoreDiagnosticListener.cs",
        "src/Qyl.Telemetry.AutoInstrumentation.SqlClient/SqlClient/SqlClientDiagnosticListener.cs",
    };

    private const string QylAbiDelegationToken = "global::Qyl.Telemetry.AutoInstrumentation.GeneratedCode.QylIntercepted";

    private static readonly Regex SystemValueWriterPattern = new(@"(?:global::)?(?:[A-Za-z0-9_]+\.)*(?:Messaging|Rpc|Db)Attributes\.(?:System|SystemName)\s*,\s*(?<value>[^,)]+)");
    private static readonly Regex SystemValueHelperCallPattern = new(@"QylActivityTags\.Set(?:Messaging|Rpc)\(\s*[^,]+,\s*(?<value>[^,)]+)");

    // Every messaging.system / rpc.system.name / db.system.name value the instrumentation writes is a
    // member of the pinned registry's value sets. A literal spelling of one in an emitting source is the
    // failure this gate exists for.
    private static readonly HashSet<string> ForbiddenSystemValueLiterals = new(StringComparer.Ordinal)
    {
        "connect_rpc", "dotnet_wcf", "dubbo", "elasticsearch", "grpc", "ibm.db2", "jsonrpc", "kafka", "masstransit",
        "mongodb", "mssql", "mysql", "nservicebus", "oracle.db", "other_sql", "postgresql", "rabbitmq", "redis", "sqlite",
    };

    // The one recorded gap: rpc.system.name enumerates connectrpc, dubbo, grpc and jsonrpc, so WCF has no
    // generated member and the value is named locally, with the gap in a comment beside it.
    private static readonly (string Literal, string Path)[] RecordedSystemValueGaps =
        [("dotnet_wcf", "src/Qyl.Telemetry.AutoInstrumentation/GeneratedCode/QylInterceptedWcfClient.cs")];

    private const string RecordedSystemValueGapComment = "rpc.system.name enumerates only connectrpc, dubbo, grpc and jsonrpc";

    public static void Verify()
    {
        var contract = ContractModel.Load();
        VerifyContractArtifacts(contract);
        VerifyCompileBindingOnlyTruthGate(contract);
        VerifyConformanceProfileGate();
        VerifyManagedEvidenceBoundaries(contract);
        VerifySupportedVersionOverrides(contract);
        VerifyRealDemoCoverage(contract);
        VerifyNativeAotEvidenceIsExecutable(contract);
        VerifyGeneratorKeys(contract);
        VerifyEnvironmentContract(contract);
        VerifySemconvAttributeContract();
        VerifyQylVocabularyLiterals();
        VerifySystemValueContract();
        VerifyDemoSolutionReleaseMapping();
        VerifyMetricContract();
        VerifySensitiveAttributeEmissionPolicy();
        VerifyBehaviorSemanticsContract();
        VerifyProductiveMechanismContract();
        Log.Information("contract-invariants-ok");
    }

    private static void VerifyContractArtifacts(Item contract)
    {
        ContractModel.VerifyContractModel(contract);
        ContractArtifacts.VerifyGeneratedFiles(contract);
    }

    // Data-level re-enforcement of the compile-binding truth predicate: an implemented
    // compile_binding_only signal cannot claim implementation or runtime verification evidence. Enforced here directly so weakening the model cannot drop it.
    private static void VerifyCompileBindingOnlyTruthGate(Item contract)
    {
        var items = ContractModel.ContractItems(contract);
        var unexpected = items
            .Where(static item => item.Get("qyl_status") is "implemented" && item.Get("evidence_level") is "compile_binding_only")
            .Select(static item => item.GetText("key"))
            .ToArray();
        Gate.Require(unexpected.Length == 0, () => $"implemented signals require runtime evidence; compile_binding_only is not implemented: {ContractValue.RenderSorted(unexpected)}");

        foreach (var item in items.Where(static item => item.Get("evidence_level") is "compile_binding_only"))
        {
            var runtimeEvidence = ContractModel.Evidence(item).Where(ContractEvidence.IsRuntimeVerification).ToList();
            Gate.Require(runtimeEvidence.Count == 0,
                () => $"compile_binding_only item must not claim runtime verification evidence for {item.GetText("key")}: {ContractValue.Render(runtimeEvidence)}");
        }
    }

    // Data-level enforcement over the conformance profile set: fixture coverage must stay
    // multi-profile, unique, and honestly shaped.
    private static void VerifyConformanceProfileGate()
    {
        var profiles = ContractModel.ConformanceProfiles;
        var profileIds = profiles.Select(static profile => profile.ProfileId).ToArray();
        var serviceNames = profiles.Select(static profile => profile.ServiceName).ToArray();
        Gate.Require(profileIds.Distinct(StringComparer.Ordinal).Count() == profileIds.Length, "duplicate conformance profile_id");
        Gate.Require(serviceNames.Distinct(StringComparer.Ordinal).Count() == serviceNames.Length, "duplicate conformance service_name");
        var ids = profileIds.ToHashSet(StringComparer.Ordinal);
        Gate.Require(ids.SetEquals(ContractModel.RequiredConformanceProfileIds),
            () => "conformance profiles must match required fixture profile ids: "
                  + $"missing={ContractValue.RenderSorted(ContractModel.RequiredConformanceProfileIds.Except(ids))} stale={ContractValue.RenderSorted(ids.Except(ContractModel.RequiredConformanceProfileIds))}");
        Gate.Require(profileIds.Length >= 8, "conformance plan must not collapse back to a narrow single-demo profile set");
        foreach (var profile in profiles)
        {
            if (profile.ProfileId == "qyl-aot-unsupported-nativeaot")
                Gate.Require(profile.SignalNames.Length == 0, "unsupported NativeAOT conformance profile must remain empty");
            else
                Gate.Require(profile.SignalNames.Length > 0, $"conformance profile must remain non-empty: {profile.ProfileId}");
        }
    }

    private static void VerifyManagedEvidenceBoundaries(Item contract)
    {
        var managedKeys = ContractModel.ImplementedSignalItems(contract)
            .Where(static item => item.Get("evidence_level") is "verified_managed")
            .Select(static item => item.GetText("key"))
            .ToHashSet(StringComparer.Ordinal);
        Gate.Require(managedKeys.SetEquals(ManagedEvidenceNativeAotBoundaryKeys),
            () => "verified_managed implemented signals must be an explicit NativeAOT boundary set: "
                  + $"missing_boundary={ContractValue.RenderSorted(managedKeys.Except(ManagedEvidenceNativeAotBoundaryKeys))} "
                  + $"stale_boundary={ContractValue.RenderSorted(ManagedEvidenceNativeAotBoundaryKeys.Except(managedKeys))}");
    }

    private static void VerifySupportedVersionOverrides(Item contract)
    {
        var itemsByKey = ContractModel.ContractItems(contract).ToDictionary(static item => item.GetText("key"), StringComparer.Ordinal);
        foreach (var (key, expected) in ContractModel.QylSupportedVersionOverrides)
        {
            var actual = itemsByKey[key].GetText("supported_versions");
            Gate.Require(actual == expected, $"qyl supported-version override mismatch for {key}: expected={ContractValue.Render(expected)} actual={ContractValue.Render(actual)}");
        }
    }

    /// <summary>
    /// The demo lanes CheckFull runs and the lanes the contract cites as evidence are one set: a cited lane
    /// file must be a registered lane, and every registered lane must be claimed by an implemented signal.
    /// </summary>
    private static void VerifyRealDemoCoverage(Item contract)
    {
        var cited = ContractModel.ImplementedSignalItems(contract)
            .SelectMany(ContractModel.Evidence)
            .Where(static entry => entry.StartsWith("tests/Qyl.Telemetry.Integration.Tests/Lanes/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var registered = DemoLanes.All.Select(static lane => lane.EvidencePath).ToHashSet(StringComparer.Ordinal);
        var missing = cited.Except(registered).ToArray();
        Gate.Require(missing.Length == 0, () => $"real demo lane evidence missing from the Demos lane registry: {ContractValue.RenderSorted(missing)}");
        var unclaimed = registered.Except(cited).ToArray();
        Gate.Require(unclaimed.Length == 0, () => $"registered demo lane is not claimed by contract evidence: {ContractValue.RenderSorted(unclaimed)}");
    }

    private static void VerifyNativeAotEvidenceIsExecutable(Item contract)
    {
        foreach (var item in ContractModel.ImplementedSignalItems(contract).Where(static item => item.Get("evidence_level") is "verified_nativeaot"))
        {
            var executes = ContractModel.Evidence(item).Select(DemoLanes.ByEvidencePath).Any(static lane => lane is { RunsNativeAot: true });
            Gate.Require(executes, $"verified_nativeaot item has no demo lane that executes its NativeAOT binary: {item.GetText("key")}");
        }
    }

    private static IReadOnlyList<AbsolutePath> GeneratorPartialPaths() =>
        GeneratorPath.Parent.GlobFiles("QylAutoInstrumentationGenerator*.cs").OrderBy(static path => path.ToString(), StringComparer.Ordinal).ToArray();

    private static string? _generatorSources;

    private static string ReadGeneratorSources() => _generatorSources ??= string.Join("\n", GeneratorPartialPaths().Select(static path => File.ReadAllText(path)));

    private static IReadOnlyList<AbsolutePath> DeclarationPaths() =>
        (RuntimeSourceRoot / "GeneratedCode").GlobFiles("QylIntercepted*.cs").OrderBy(static path => path.ToString(), StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<AbsolutePath> SourceFiles(AbsolutePath root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(static path => (AbsolutePath)path).ToArray();

    /// <summary>instrumentation id value → the constant name that declares it.</summary>
    private static Dictionary<string, string> ParseInstrumentationIdConstants()
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in InstrumentationIdConstant().Matches(File.ReadAllText(IdsPath)))
            constants[match.Groups[2].Value] = match.Groups[1].Value;
        return constants;
    }

    private static string ResolveInstrumentationId(string constantName, Dictionary<string, string> idConstants, string context)
    {
        foreach (var (instrumentationId, candidate) in idConstants)
        {
            if (candidate == constantName)
                return instrumentationId;
        }

        throw Gate.Fail($"{context} references unknown QylAutoInstrumentationIds.{constantName}");
    }

    /// <summary>The argument text of every <c>[attribute(...)]</c>, nested parentheses balanced.</summary>
    private static List<string> AttributeArguments(string text, string attribute)
    {
        var arguments = new List<string>();
        var marker = $"[{attribute}(";
        var position = 0;
        while (true)
        {
            var start = text.IndexOf(marker, position, StringComparison.Ordinal);
            if (start < 0)
                return arguments;
            var depth = 0;
            var index = start + marker.Length - 1;
            var closed = false;
            for (; index < text.Length; index++)
            {
                if (text[index] == '(')
                    depth++;
                else if (text[index] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closed = true;
                        break;
                    }
                }
            }

            Gate.Require(closed, $"unterminated [{attribute}(...)] declaration");
            arguments.Add(text[(start + marker.Length)..index]);
            position = index + 1;
        }
    }

    private sealed record Intercept(string Shape, string Body, string Kind);

    private sealed record IntegrationDeclaration(string Name, AbsolutePath Path, string InstrumentationId, string Signal, string[] MetricIds, Intercept[] Intercepts);

    /// <summary>The interceptor-lane declarations: one [QylIntegration] class per integration with its
    /// [QylIntercept] rows — the single source the generator derives its catalog from.</summary>
    private static List<IntegrationDeclaration> ParseIntegrationDeclarations()
    {
        var declarations = new List<IntegrationDeclaration>();
        foreach (var path in DeclarationPaths())
        {
            var text = StripCSharpComments(File.ReadAllText(path));
            var integrations = AttributeArguments(text, "QylIntegration");
            if (integrations.Count == 0)
                continue;
            Gate.Require(integrations.Count == 1, $"a declaration class carries exactly one [QylIntegration]: {Repo.Relative(path)}");
            var classMatch = InterceptedClass().Match(text);
            Gate.Require(classMatch.Success, $"[QylIntegration] must decorate a public static QylIntercepted* class: {Repo.Relative(path)}");
            var name = classMatch.Groups[1].Value["QylIntercepted".Length..];
            var integration = integrations[0];
            var head = IntegrationHead().Match(integration);
            Gate.Require(head.Success, $"[QylIntegration] must name its id through the vocabulary constants: {Repo.Relative(path)}");
            var signalMatch = IntegrationSignal().Match(integration);
            var metricMatch = IntegrationMetricIds().Match(integration);
            var methodNames = PublicStaticMethod().Matches(text).Select(static match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var intercepts = new List<Intercept>();
            foreach (var arguments in AttributeArguments(text, "QylIntercept"))
            {
                var shape = InterceptShape().Match(arguments);
                Gate.Require(shape.Success, $"[QylIntercept] must select a named QylShapes predicate: {Repo.Relative(path)}");
                var start = NamedMember("Start").Match(arguments);
                var body = InterceptBody().Match(arguments);
                foreach (var member in new[] { "Start", "Enrich", "Metric" })
                {
                    var named = NamedMember(member).Match(arguments);
                    Gate.Require(!named.Success || methodNames.Contains(named.Groups[1].Value),
                        $"[QylIntercept] {member} names a missing helper method {named.Groups[1].Value}: {Repo.Relative(path)}");
                }

                var bodyName = body.Success ? body.Groups[1].Value : "Trace";
                intercepts.Add(new Intercept(shape.Groups[1].Value, bodyName, $"{name}.{(start.Success ? start.Groups[1].Value : bodyName)}"));
            }

            Gate.Require(intercepts.Count > 0, $"[QylIntegration] class declares no [QylIntercept] rows: {Repo.Relative(path)}");
            declarations.Add(new IntegrationDeclaration(
                name,
                path,
                head.Groups[1].Value,
                signalMatch.Success ? signalMatch.Groups[1].Value : "Traces",
                metricMatch.Success ? MetricIdConstant().Matches(metricMatch.Groups[1].Value).Select(static match => match.Groups[1].Value).ToArray() : [],
                intercepts.ToArray()));
        }

        Gate.Require(declarations.Count > 0, "no [QylIntegration] declarations found in the runtime");
        return declarations;
    }

    private static Regex NamedMember(string member) => new(member + @"\s*=\s*nameof\(([A-Za-z0-9]+)\)");

    /// <summary>Every [QylSignal(id, signal)] in the runtime: the lanes that own a signal outside the
    /// interceptor lane, plus the DbCommand provider fan-out.</summary>
    private static List<(string ConstantName, string Signal)> ParseSignalDeclarations()
    {
        var declarations = new List<(string, string)>();
        foreach (var path in SourceFiles(RuntimeSourceRoot))
        {
            var text = StripCSharpComments(File.ReadAllText(path));
            foreach (var arguments in AttributeArguments(text, "QylSignal"))
            {
                var match = SignalDeclaration().Match(arguments);
                Gate.Require(match.Success, $"[QylSignal] must name its id and signal through the vocabulary constants: {Repo.Relative(path)}");
                declarations.Add((match.Groups[1].Value, match.Groups[2].Value));
            }
        }

        return declarations;
    }

    /// <summary>The per-signal instrumentation-id sets the generator derives QylDeclaredInstrumentations from.</summary>
    private static Dictionary<string, HashSet<string>> DeclaredSignalIds(Dictionary<string, string> idConstants)
    {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["traces"] = new(StringComparer.Ordinal),
            ["metrics"] = new(StringComparer.Ordinal),
            ["logs"] = new(StringComparer.Ordinal),
        };
        foreach (var declaration in ParseIntegrationDeclarations())
        {
            var context = $"[QylIntegration] on {Repo.Relative(declaration.Path)}";
            declared[declaration.Signal.ToLowerInvariant()].Add(ResolveInstrumentationId(declaration.InstrumentationId, idConstants, context));
            foreach (var metricId in declaration.MetricIds)
                declared["metrics"].Add(ResolveInstrumentationId(metricId, idConstants, context));
        }

        foreach (var (constantName, signal) in ParseSignalDeclarations())
            declared[signal.ToLowerInvariant()].Add(ResolveInstrumentationId(constantName, idConstants, "[QylSignal]"));
        return declared;
    }

    /// <summary>Literal values, plus registry scope symbols resolved through the emitter's PascalCase rule
    /// (QylTelemetryNames.Scopes.SystemRuntime is the constant "System.Runtime").</summary>
    private static Dictionary<string, string> ParseStringConstants(string text)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in StringConstant().Matches(text))
            constants[match.Groups[1].Value] = match.Groups[2].Value;
        var scopeSymbols = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in RequiredRegisteredMeterNameValues)
            scopeSymbols[RegistryScopeSymbol(value)] = value;
        foreach (Match match in ScopeConstant().Matches(text))
        {
            if (!scopeSymbols.TryGetValue(match.Groups[2].Value, out var value))
                throw Gate.Fail($"unknown registry scope symbol QylTelemetryNames.Scopes.{match.Groups[2].Value} in {match.Groups[1].Value}");
            constants[match.Groups[1].Value] = value;
        }

        return constants;
    }

    /// <summary>Mirrors the semantic-conventions emitter: split on ".-_ ", capitalise each piece.</summary>
    private static string RegistryScopeSymbol(string value) =>
        string.Concat(Regex.Split(value, @"[.\-_ ]").Where(static piece => piece.Length > 0).Select(static piece => char.ToUpperInvariant(piece[0]) + piece[1..]));

    private static void VerifyUndeclaredImplementedSignals(List<Item> implementedSignals)
    {
        var implementedKeys = implementedSignals.Select(static item => item.GetText("key")).ToHashSet(StringComparer.Ordinal);
        var stale = UndeclaredImplementedSignalKeys.Except(implementedKeys).ToArray();
        Gate.Require(stale.Length == 0, () => $"undeclared-implemented exemption names non-implemented signals: {ContractValue.RenderSorted(stale)}");
        var declared = DeclaredSignalIds(ParseInstrumentationIdConstants()).Values.SelectMany(static ids => ids).ToHashSet(StringComparer.Ordinal);
        var redundant = implementedSignals
            .Where(item => UndeclaredImplementedSignalKeys.Contains(item.GetText("key")) && declared.Contains(item.GetText("instrumentation_id")))
            .Select(static item => item.GetText("key"))
            .ToArray();
        Gate.Require(redundant.Length == 0, () => $"undeclared-implemented exemption names declared instrumentations: {ContractValue.RenderSorted(redundant)}");
    }

    private static void VerifyEnvironmentContract(Item contract)
    {
        var items = ContractModel.ContractItems(contract);
        var options = File.ReadAllText(OptionsPath);
        var idConstants = ParseInstrumentationIdConstants();

        var controls = items.Where(static item => item.GetText("kind") == ContractModel.ControlKind).ToList();
        var instrumentationOptions = items.Where(static item => item.GetText("kind") == ContractModel.OptionKind).ToList();
        Gate.Require(controls.Count == 7, $"wrong YAML global environment control count: {controls.Count}");
        Gate.Require(instrumentationOptions.Count == 19, $"wrong YAML instrumentation option count: {instrumentationOptions.Count}");

        foreach (var item in controls)
        {
            var variable = item.GetText("environment_variable");
            if (variable.Contains("{0}", StringComparison.Ordinal))
            {
                var split = variable.IndexOf("{0}", StringComparison.Ordinal);
                var expression = $"\"{variable[..split]}\" + instrumentationId + \"{variable[(split + 3)..]}\"";
                Gate.Require(options.Contains(expression, StringComparison.Ordinal), $"global signal-specific control is not generated by QylAutoInstrumentationOptions: {variable}");
            }
            else
            {
                Gate.Require(options.Contains(variable, StringComparison.Ordinal), $"global control is not read by QylAutoInstrumentationOptions: {variable}");
            }
        }

        foreach (var item in instrumentationOptions)
        {
            var variable = item.GetText("environment_variable");
            var status = item.Get("qyl_status");
            Gate.Require(!(status is "option_bound" && !options.Contains(variable, StringComparison.Ordinal)), $"instrumentation option is not read by QylAutoInstrumentationOptions: {variable}");
            Gate.Require(!(status is "unsupported_nativeaot" && options.Contains(variable, StringComparison.Ordinal)), $"unsupported instrumentation option must be absent from QylAutoInstrumentationOptions: {variable}");
        }

        var implementedSignals = ContractModel.ImplementedSignalItems(contract);
        VerifyUndeclaredImplementedSignals(implementedSignals);

        // Coherence: the enabled-set each environment toggle binds to is generated from the runtime's
        // declarations; the declarations must therefore cover exactly the implemented contract signals.
        var actualBySignal = DeclaredSignalIds(idConstants);
        foreach (var signal in new[] { "traces", "metrics", "logs" })
        {
            var expected = implementedSignals
                .Where(item => item.Get("signal") is string value && value == signal && !UndeclaredImplementedSignalKeys.Contains(item.GetText("key")))
                .Select(static item => item.GetText("instrumentation_id"))
                .ToHashSet(StringComparer.Ordinal);
            var actual = actualBySignal[signal];
            Gate.Require(expected.SetEquals(actual),
                () => $"{signal} declared instrumentation id set mismatch: missing={ContractValue.RenderSorted(expected.Except(actual))} extra={ContractValue.RenderSorted(actual.Except(expected))}");
        }

        foreach (var member in new[] { "QylDeclaredInstrumentations.Traces", "QylDeclaredInstrumentations.Metrics", "QylDeclaredInstrumentations.Logs" })
            Gate.Require(options.Contains(member, StringComparison.Ordinal), $"QylAutoInstrumentationOptions must bind the environment toggles to the generated declaration set: {member}");
        Gate.Require(!options.Contains("private static readonly string[]", StringComparison.Ordinal),
            "QylAutoInstrumentationOptions must not retype an instrumentation id array beside the generated declaration set");
    }

    private static void VerifySemconvAttributeContract()
    {
        var runtimeProject = File.ReadAllText(RuntimeProjectPath);
        foreach (var package in new[] { "Include=\"Qyl.Telemetry.SemanticConventions\"", "Include=\"Qyl.Telemetry.SemanticConventions.Incubating\"" })
            Gate.Require(runtimeProject.Contains(package, StringComparison.Ordinal), $"runtime project must reference generated semconv package: {package}");

        // External contract: every attribute key an emitting source writes is a generated semconv
        // constant, named directly at the call site.
        var emissionSources = RuntimeEmissionRoots.SelectMany(SourceFiles).Select(static path => (Path: path, Text: File.ReadAllText(path))).ToArray();
        foreach (var ns in new[] { "Qyl.Telemetry.SemanticConventions.Attributes.", "Qyl.Telemetry.SemanticConventions.Incubating.Attributes." })
            Gate.Require(emissionSources.Any(source => source.Text.Contains(ns, StringComparison.Ordinal)), $"runtime attribute keys must come from generated semconv constants in {ns}");

        foreach (var (path, text) in emissionSources)
        {
            foreach (var pattern in ForbiddenAttributeEmissionLiteralPatterns)
                Gate.Require(!pattern.IsMatch(text), $"runtime telemetry attribute emission must not use literal keys: {Repo.Relative(path)}");
        }
    }

    /// <summary>The C# this repository is answerable for — what git tracks, nothing else. A directory
    /// walk would also read a registry checkout cloned into .semconv/ and report its analyzer test cases
    /// as qyl writing key literals.</summary>
    private static string[] OwnedCSharpFiles()
    {
        var listing = Shell.Run("git", ["-C", Repo.Root, "ls-files", "-z", "--", "*.cs"], Repo.Root);
        Gate.Require(listing.ExitCode == 0, $"git ls-files failed: {listing.Stderr.Trim()}");
        var files = listing.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Gate.Require(files.Length > 0, "git ls-files returned no C# files; the invariant would pass without checking anything");
        return files;
    }

    /// <summary>
    /// Registry keys are generated constants, never literals. Under src/ the key literal is forbidden
    /// outright. Outside src/ — demos, the build project and its fixtures — only a WRITE is forbidden: a
    /// demo that asserts on what qyl emitted has to name the key literally.
    /// </summary>
    private static void VerifyQylVocabularyLiterals()
    {
        foreach (var (keyLiteral, message) in new[]
                 {
                     (QylVocabularyKeyLiteral, "qyl.* vocabulary must come from the generated semconv constants, not literals"),
                     (SessionIdKeyLiteral, "session.id must come from SessionAttributes.Id, not a literal"),
                 })
        {
            var anywhere = new Regex(keyLiteral);
            var writers = QylVocabularyWriterPatterns.Select(pattern => new Regex(pattern.Replace("{key}", keyLiteral, StringComparison.Ordinal))).ToArray();
            var sourceHits = new List<string>();
            var writerHits = new List<string>();
            foreach (var relative in OwnedCSharpFiles())
            {
                var path = Repo.Root / relative;
                if (path.Name.EndsWith(".g.cs", StringComparison.Ordinal) || !path.FileExists())
                    continue;
                var text = StripCSharpComments(File.ReadAllText(path));
                if (relative.StartsWith("src/", StringComparison.Ordinal))
                {
                    if (anywhere.IsMatch(text))
                        sourceHits.Add(relative);
                }
                else if (writers.Any(writer => writer.IsMatch(text)))
                {
                    writerHits.Add(relative);
                }
            }

            Gate.Require(sourceHits.Count == 0, () => $"{message}: {ContractValue.RenderSorted(sourceHits)}");
            Gate.Require(writerHits.Count == 0, () => $"{message}, and these write it: {ContractValue.RenderSorted(writerHits)}");
        }
    }

    /// <summary>messaging.system, rpc.system.name and db.system.name carry generated values only: no
    /// writer takes a literal, and no emitting source spells a registry system value out, except the one
    /// recorded gap.</summary>
    private static void VerifySystemValueContract()
    {
        var literalPattern = new Regex(@"""([^""\\]*)""");
        foreach (var root in RuntimeEmissionRoots)
        {
            foreach (var path in SourceFiles(root))
            {
                var relative = Repo.Relative(path);
                var raw = File.ReadAllText(path);
                var source = StripCSharpComments(raw);
                foreach (var pattern in new[] { SystemValueWriterPattern, SystemValueHelperCallPattern })
                {
                    foreach (Match match in pattern.Matches(source))
                    {
                        var value = match.Groups["value"].Value.Trim();
                        Gate.Require(!value.StartsWith('"'), $"system value must not be written as a literal: {relative}: {value}");
                    }
                }

                foreach (Match literal in literalPattern.Matches(source))
                {
                    var value = literal.Groups[1].Value;
                    if (!ForbiddenSystemValueLiterals.Contains(value))
                        continue;
                    Gate.Require(RecordedSystemValueGaps.Contains((value, relative)), $"registry system value must not be spelled as a literal: {relative}: {ContractValue.Render(value)}");
                    Gate.Require(raw.Contains(RecordedSystemValueGapComment, StringComparison.Ordinal), $"recorded system-value gap must keep the comment naming it: {relative}");
                }
            }
        }

        foreach (var (literal, path) in RecordedSystemValueGaps)
            Gate.Require(File.ReadAllText(Repo.Root / path).Contains(literal, StringComparison.Ordinal), $"recorded system-value gap is stale, {ContractValue.Render(literal)} is gone from {path}");
    }

    private static void VerifyDemoSolutionReleaseMapping()
    {
        var listed = DemoSolutionProject().Matches(File.ReadAllText(Repo.DemoSolution)).Select(static match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var sources = Directory.EnumerateFiles(Repo.Src, "*.csproj", SearchOption.AllDirectories).Select(static path => Repo.Relative((AbsolutePath)path)).ToHashSet(StringComparer.Ordinal);
        var missing = sources.Except(listed).ToArray();
        Gate.Require(missing.Length == 0,
            () => $"demo solution must list every source project so Release configuration propagates to project references: {ContractValue.RenderSorted(missing)}");
    }

    private static void VerifyMetricContract()
    {
        var meters = File.ReadAllText(MetricMetersPath);
        var dbClientMetrics = File.ReadAllText(DbClientMetricsPath);
        var options = File.ReadAllText(OptionsPath);
        var generator = ReadGeneratorSources();

        var meterConstants = ParseStringConstants(meters);
        var meterTable = MeterTable().Match(meters);
        Gate.Require(meterTable.Success, "QylMetricMeters must select meters through the MetricMeterTable");
        var registered = Identifier().Matches(meterTable.Groups[1].Value)
            .Select(static match => match.Value)
            .Where(meterConstants.ContainsKey)
            .Select(name => meterConstants[name])
            .ToHashSet(StringComparer.Ordinal);
        var missingMeters = RequiredRegisteredMeterNameValues.Except(registered).ToArray();
        Gate.Require(missingMeters.Length == 0, () => $"QylMetricMeters must register required well-known meter names: {ContractValue.RenderSorted(missingMeters)}");
        var forbiddenMeters = ForbiddenRegisteredMeterNameValues.Intersect(registered).ToArray();
        Gate.Require(forbiddenMeters.Length == 0, () => $"QylMetricMeters must not register superseded native meter names: {ContractValue.RenderSorted(forbiddenMeters)}");

        // Every instrument qyl creates is a registry row: name and unit come off a MetricDefinition.
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in RuntimeEmissionRoots)
        {
            foreach (var path in SourceFiles(root))
            {
                var relative = Repo.Relative(path);
                foreach (Match match in MetricInstrumentFactory().Matches(StripCSharpComments(File.ReadAllText(path))))
                {
                    var name = match.Groups["name"].Value.Trim();
                    var unit = match.Groups["unit"].Value.Trim();
                    Gate.Require(name.EndsWith(".Name", StringComparison.Ordinal) && unit.EndsWith(".Unit", StringComparison.Ordinal),
                        $"instrument must be created from a MetricDefinition row: {relative}: {name}, {unit}");
                    var definition = name[..^".Name".Length];
                    Gate.Require(unit[..^".Unit".Length] == definition, $"instrument name and unit must come from one definition: {relative}: {name}, {unit}");
                    created.Add(string.Join('.', definition.Split('.').TakeLast(2)));
                }
            }
        }

        Gate.Require(created.SetEquals(QylOwnedMetricDefinitions),
            () => "the instruments qyl creates must be exactly the declared registry definitions: "
                  + $"missing={ContractValue.RenderSorted(QylOwnedMetricDefinitions.Except(created))} extra={ContractValue.RenderSorted(created.Except(QylOwnedMetricDefinitions))}");

        Gate.Require(options.Contains(MetricsAdditionalSourcesVariable, StringComparison.Ordinal),
            $"QylAutoInstrumentationOptions must preserve upstream additional metric source support: {MetricsAdditionalSourcesVariable}");
        // Coherence: the option read from the additional-sources variable must flow into meter
        // registration. Both member names are derived from the variable value, so renames stay free.
        var variableConstant = new Regex($"const string ([A-Za-z0-9_]+) = \"{MetricsAdditionalSourcesVariable}\";").Match(options);
        Gate.Require(variableConstant.Success, "additional metric sources variable must be a named constant in QylAutoInstrumentationOptions");
        var readSites = options.Split(variableConstant.Groups[1].Value);
        string? additionalSourcesMember = null;
        foreach (Match match in InternalStringArrayMember().Matches(options))
        {
            if (readSites.Any(segment => segment.Contains(match.Groups[1].Value, StringComparison.Ordinal)))
                additionalSourcesMember = match.Groups[1].Value;
        }

        Gate.Require(additionalSourcesMember is not null && meters.Contains($"options.{additionalSourcesMember}", StringComparison.Ordinal),
            "QylMetricMeters must append the additional metric sources option to registered meters");

        foreach (var token in new[] { "NavigationManager", "NavigateTo" })
        {
            Gate.Require(!generator.Contains(token, StringComparison.Ordinal) && !meters.Contains(token, StringComparison.Ordinal) && !dbClientMetrics.Contains(token, StringComparison.Ordinal),
                $"productive code must not synthesize source-visible ASP.NET Core component metrics: {token}");
        }
    }

    private static void VerifySensitiveAttributeEmissionPolicy()
    {
        // The repository's redaction/opt-in controls must gate every sensitive raw value. Positive
        // anchors are limited to the control points.
        var policy = File.ReadAllText(SensitiveCapturePolicyPath);
        foreach (var token in new[] { "QylCaptureHelpers.RedactQueryValues(", "AspNetCoreUrlQueryRedactionDisabled" })
            Gate.Require(policy.Contains(token, StringComparison.Ordinal), $"QylSensitiveCapturePolicy must implement the redaction/opt-in controls: {token}");

        // The outbound HTTP span is the BCL's and it redacts its own url.full; the consumer's opt-out
        // flips the runtime switch, and QylInstrumentation is where that single write lives.
        var activation = File.ReadAllText(RuntimeSourceRoot / "QylInstrumentation.cs");
        foreach (var token in new[] { "HttpClientUrlQueryRedactionDisabled", "System.Net.Http.DisableUriRedaction" })
            Gate.Require(activation.Contains(token, StringComparison.Ordinal), $"QylInstrumentation must bind the HttpClient url query redaction opt-out: {token}");

        var stale = DbQueryTextAllowedPaths.Where(static relative => !File.Exists(Repo.Root / relative)).ToArray();
        Gate.Require(stale.Length == 0, () => $"db.query.text writer allowlist contains missing paths: {ContractValue.RenderSorted(stale)}");

        foreach (var root in RuntimeEmissionRoots)
        {
            foreach (var path in SourceFiles(root))
            {
                var relative = Repo.Relative(path);
                var text = File.ReadAllText(path);
                foreach (var pattern in SensitiveRawSetTagPatterns)
                {
                    var match = pattern.Match(text);
                    Gate.Require(!match.Success || SensitiveRawSetTagAllowedPaths.Contains(relative),
                        $"sensitive raw attribute writes must go through QylSensitiveCapturePolicy: {relative} {match.Value}");
                }

                foreach (var pattern in SensitiveSemanticWriterPatterns)
                {
                    var match = pattern.Match(text);
                    Gate.Require(!match.Success, $"runtime-public sensitive writes must go through the owning semantics helper: {relative} {match.Value}");
                }

                Gate.Require(!text.Contains("QylCaptureHelpers.FormatUrlFull(", StringComparison.Ordinal) || UrlFormatAllowedPaths.Contains(relative),
                    $"url.full formatting must stay centralized behind the sensitive capture policy: {relative}");
                Gate.Require(!text.Contains("QylCaptureHelpers.RedactQueryValues(", StringComparison.Ordinal) || UrlFormatAllowedPaths.Contains(relative),
                    $"url query redaction must stay centralized behind sensitive capture policy/helpers: {relative}");

                if (!DbQueryTextWritePattern.IsMatch(text))
                    continue;
                Gate.Require(DbQueryTextAllowedPaths.Contains(relative), $"db.query.text writes must be owned by typed DB listener paths: {relative}");
                foreach (Match match in DbQueryTextWritePattern.Matches(text))
                {
                    var window = text[Math.Max(0, match.Index - 260)..match.Index];
                    Gate.Require(window.Contains("if (DatabaseSemantics.ShouldWriteQueryText(", StringComparison.Ordinal),
                        $"db.query.text write must be guarded by DatabaseSemantics.ShouldWriteQueryText: {relative}");
                }
            }
        }
    }

    private static List<string> FindCatchBlocks(string text)
    {
        var blocks = new List<string>();
        var position = 0;
        while (true)
        {
            var match = CatchKeyword().Match(text, position);
            if (!match.Success)
                return blocks;
            var brace = text.IndexOf('{', match.Index);
            Gate.Require(brace >= 0, "catch block without body");
            var depth = 0;
            var closed = false;
            for (var index = brace; index < text.Length; index++)
            {
                if (text[index] == '{')
                    depth++;
                else if (text[index] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        blocks.Add(text[brace..(index + 1)]);
                        position = index + 1;
                        closed = true;
                        break;
                    }
                }
            }

            Gate.Require(closed, "unterminated catch block");
        }
    }

    private static void VerifyBehaviorSemanticsContract()
    {
        var generator = ReadGeneratorSources();
        Gate.Require(generator.Contains(QylAbiDelegationToken, StringComparison.Ordinal), "generator must delegate intercepted call-sites to the Qyl runtime instrumentation assembly");

        // The guard's subject is the source the generator WRITES, which leaves it only through
        // builder.Append*. A diagnostic message may still say "ActivitySource" to the consumer.
        var emitted = string.Join("\n", GeneratorEmission().Matches(StripCSharpComments(generator)).Select(static match => match.Groups["argument"].Value));
        foreach (var token in ForbiddenGeneratorInlineTelemetryTokens)
            Gate.Require(!emitted.Contains(token, StringComparison.Ordinal), $"generator must not inline telemetry behavior instead of delegating to runtime: {token}");

        foreach (var path in GeneratorPartialPaths().Concat(DeclarationPaths()))
        {
            var text = File.ReadAllText(path);
            foreach (var token in ForbiddenExceptionRewriteTokens)
                Gate.Require(!text.Contains(token, StringComparison.Ordinal), $"interceptor must preserve exception stack semantics; forbidden token in {Repo.Relative(path)}: {token}");
            foreach (var block in FindCatchBlocks(text))
                Gate.Require(block.Contains("throw;", StringComparison.Ordinal), $"interceptor catch block must rethrow with throw; in {Repo.Relative(path)}");
        }
    }

    public static string StripCSharpComments(string text)
    {
        var result = new StringBuilder(text.Length);
        var index = 0;
        var inBlockComment = false;
        while (index < text.Length)
        {
            if (inBlockComment)
            {
                if (string.CompareOrdinal(text, index, "*/", 0, 2) == 0)
                {
                    inBlockComment = false;
                    index += 2;
                }
                else
                {
                    if (text[index] is '\r' or '\n')
                        result.Append(text[index]);
                    index++;
                }

                continue;
            }

            if (string.CompareOrdinal(text, index, "/*", 0, 2) == 0)
            {
                inBlockComment = true;
                index += 2;
                continue;
            }

            if (string.CompareOrdinal(text, index, "//", 0, 2) == 0)
            {
                while (index < text.Length && text[index] is not ('\r' or '\n'))
                    index++;
                continue;
            }

            result.Append(text[index]);
            index++;
        }

        return result.ToString();
    }

    private static void VerifyProductiveMechanismContract()
    {
        foreach (var root in ProductiveMechanismRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj"))
                    continue;
                var extension = Path.GetExtension(file);
                if (extension is not (".cs" or ".props" or ".targets"))
                    continue;
                var text = File.ReadAllText(file);
                var scan = extension == ".cs" ? StripCSharpComments(text) : text;
                foreach (var token in ForbiddenMechanismTokens)
                    Gate.Require(!scan.Contains(token, StringComparison.Ordinal), $"productive code must not use forbidden instrumentation mechanism {token}: {Repo.Relative((AbsolutePath)file)}");
            }
        }
    }

    private static List<string> ParseEnumMembers(string text, string enumName)
    {
        var start = text.IndexOf($"enum {enumName}", StringComparison.Ordinal);
        Gate.Require(start >= 0, $"{enumName} enum missing");
        var rest = text[(start + $"enum {enumName}".Length)..];
        var end = rest.IndexOf('}');
        var block = end >= 0 ? rest[..end] : rest;
        return EnumMember().Matches(block).Select(static match => match.Groups[1].Value).ToList();
    }

    private static HashSet<string> ParseDeclaredShapes()
    {
        var text = StripCSharpComments(File.ReadAllText(DeclarationsPath));
        var start = text.IndexOf("public static class QylShapes", StringComparison.Ordinal);
        Gate.Require(start >= 0, "QylShapes missing from the runtime declarations");
        var rest = text[(start + "public static class QylShapes".Length)..];
        var end = rest.IndexOf("\n}", StringComparison.Ordinal);
        var block = end >= 0 ? rest[..end] : rest;
        var shapes = ShapeConstant().Matches(block)
            .Where(static match => match.Groups[1].Value == match.Groups[2].Value)
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Gate.Require(shapes.Count > 0, "QylShapes declares no shape predicates");
        return shapes;
    }

    private static HashSet<string> ParseGeneratorShapeCases(string generator)
    {
        var start = generator.IndexOf("private static bool TryMatchShape(", StringComparison.Ordinal);
        Gate.Require(start >= 0, "TryMatchShape dispatch missing from generator");
        var rest = generator[(start + "private static bool TryMatchShape(".Length)..];
        var end = rest.IndexOf("\n    private static ", StringComparison.Ordinal);
        var block = end >= 0 ? rest[..end] : rest;
        var cases = ShapeCase().Matches(block).Select(static match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Gate.Require(cases.Count > 0, "TryMatchShape dispatches no named shape");
        return cases;
    }

    private static HashSet<string> VerifyGeneratedInterceptorManifests(List<Item> manifests, HashSet<string> interceptorKinds, string context)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifests.Count; index++)
        {
            var kind = ContractValue.Text(manifests[index]["interceptorKind"]);
            Gate.Require(interceptorKinds.Contains(kind), $"{context} manifest {index} has unknown interceptor kind: {ContractValue.Render(kind)}");
            emitted.UnionWith(ContractModel.Strings(manifests[index]["contractKeys"]));
        }

        return emitted;
    }

    /// <summary>The catalog is the runtime's declarations; the generator keeps only the named shape
    /// predicates and the closed set of body templates. Returns the declared kinds.</summary>
    private static HashSet<string> VerifyInterceptorStructure(string generator)
    {
        var declarations = ParseIntegrationDeclarations();
        var kinds = declarations.SelectMany(static declaration => declaration.Intercepts).Select(static intercept => intercept.Kind).ToHashSet(StringComparer.Ordinal);
        var names = declarations.Select(static declaration => declaration.Name).ToArray();
        Gate.Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length, () => $"integration declaration names must be unique: {ContractValue.RenderSorted(names)}");

        // Philosophy guard: no hand-coded per-integration matcher or catalog may reappear.
        foreach (var (pattern, message) in new[]
                 {
                     (@"\bTryGet[A-Za-z0-9]+Invocation\(", "generator must not reintroduce hand-coded per-integration TryGet*Invocation matchers"),
                     (@"\benum InterceptorKind\b", "generator must not reintroduce a per-integration InterceptorKind enum"),
                     (@"\bInterceptorEmissionDescriptor\b|\bInterceptorMatcherDescriptor\b", "generator must not reintroduce a retyped interceptor catalog"),
                 })
        {
            foreach (Match match in Regex.Matches(generator, pattern))
                Gate.Require(match.Value == "TryGetInvocation(", message);
        }

        foreach (var token in new[]
                 {
                     "GetCatalog(compilation)",
                     "foreach (var integration in catalog.Integrations)",
                     "TryMatchDeclaration(integration, intercept, symbol, receiverType, out target, out var declaredShape)",
                     "TryMatchShape(intercept.Shape, integration, symbol, receiverType, out var shape)",
                     "EmitInterceptorManifest(builder, in target);",
                     "EmitInterceptorBody(builder, in invocation, index);",
                 })
        {
            Gate.Require(generator.Contains(token, StringComparison.Ordinal), $"generator must drive matching and emission from the runtime declarations: {token}");
        }

        var emitterStart = generator.IndexOf("private static void EmitInterceptorManifest(", StringComparison.Ordinal);
        Gate.Require(emitterStart >= 0, "generated interceptor manifest emitter missing");
        var emitterRest = generator[(emitterStart + "private static void EmitInterceptorManifest(".Length)..];
        var emitterEnd = emitterRest.IndexOf("private static string GetContractKey(", StringComparison.Ordinal);
        var manifestEmitter = emitterEnd >= 0 ? emitterRest[..emitterEnd] : emitterRest;
        foreach (var token in new[]
                 {
                     "target.Intercept.Kind",
                     "target.Integration.Signal",
                     "target.InstrumentationId",
                     "target.AdditionalMetricIds",
                     "GetContractKey(target.Integration.Signal, target.InstrumentationId)",
                     "foreach (var metricId in target.AdditionalMetricIds)",
                     "GetContractKey(TelemetrySignal.Metrics, metricId)",
                 })
        {
            Gate.Require(manifestEmitter.Contains(token, StringComparison.Ordinal), $"generated interceptor manifest must derive its representation from the declaration: {token}");
        }

        // Coherence: declared shapes and the generator's dispatch name the same predicates.
        var declaredShapes = ParseDeclaredShapes();
        var generatorShapes = ParseGeneratorShapeCases(generator);
        Gate.Require(declaredShapes.SetEquals(generatorShapes),
            () => "QylShapes and the generator's TryMatchShape dispatch must name the same predicates: "
                  + $"undispatched={ContractValue.RenderSorted(declaredShapes.Except(generatorShapes))} undeclared={ContractValue.RenderSorted(generatorShapes.Except(declaredShapes))}");
        var selectedShapes = declarations.SelectMany(static declaration => declaration.Intercepts).Select(static intercept => intercept.Shape).ToHashSet(StringComparer.Ordinal);
        Gate.Require(!selectedShapes.Except(declaredShapes).Any(), () => $"declarations select shapes QylShapes does not declare: {ContractValue.RenderSorted(selectedShapes.Except(declaredShapes))}");
        Gate.Require(!declaredShapes.Except(selectedShapes).Any(), () => $"QylShapes declares predicates no declaration selects: {ContractValue.RenderSorted(declaredShapes.Except(selectedShapes))}");

        // Coherence: the generator reads QylInterceptorBody by value, so both enums agree member-for-member.
        var runtimeBodies = ParseEnumMembers(StripCSharpComments(File.ReadAllText(DeclarationsPath)), "QylInterceptorBody");
        var generatorBodies = ParseEnumMembers(generator, "InterceptorBody");
        Gate.Require(runtimeBodies.SequenceEqual(generatorBodies),
            () => $"QylInterceptorBody and the generator's InterceptorBody must agree: runtime={ContractValue.Render(runtimeBodies)} generator={ContractValue.Render(generatorBodies)}");
        var selectedBodies = declarations.SelectMany(static declaration => declaration.Intercepts).Select(static intercept => intercept.Body).ToHashSet(StringComparer.Ordinal);
        Gate.Require(!selectedBodies.Except(runtimeBodies).Any(), () => $"declarations select unknown body templates: {ContractValue.RenderSorted(selectedBodies.Except(runtimeBodies))}");
        foreach (var body in runtimeBodies)
            Gate.Require(generator.Contains($"case InterceptorBody.{body}:", StringComparison.Ordinal), $"generator must emit every declared body template: {body}");

        return kinds;
    }

    private static void VerifyGeneratorKeys(Item contract)
    {
        var generator = ReadGeneratorSources();
        // Keys are derived from TelemetrySignal + InstrumentationId; freeform key literals are a regression.
        Gate.Require(!Regex.IsMatch(generator, @"""signals\.(?:traces|metrics|logs)\."), "generator must derive contract keys structurally, never declare freeform key literals");
        var implementedKeys = ContractModel.ImplementedSignalItems(contract).Select(static item => item.GetText("key")).ToHashSet(StringComparer.Ordinal);
        var sourceInterceptorKeys = ContractModel.SourceInterceptorSignalItems(contract).Select(static item => item.GetText("key")).ToHashSet(StringComparer.Ordinal);
        var unsupportedKeys = ContractModel.UnsupportedSignalItems(contract).Select(static item => item.GetText("key")).ToHashSet(StringComparer.Ordinal);

        foreach (var token in ForbiddenGeneratorRuntimeDispatchTokens)
            Gate.Require(!generator.Contains(token, StringComparison.Ordinal), $"generator must not emit runtime dispatch instrumentation: {token}");
        foreach (var token in RequiredRoslynInterceptorContractTokens)
            Gate.Require(generator.Contains(token, StringComparison.Ordinal), $"generator must preserve Roslyn interceptor contract token: {token}");
        foreach (var token in ForbiddenRoslynInterceptorContractTokens)
            Gate.Require(!generator.Contains(token, StringComparison.Ordinal), $"generator must not synthesize interceptor locations: {token}");

        var diagnosticLocations = CountOccurrences(generator, DiagnosticLocationToken);
        Gate.Require(diagnosticLocations <= DiagnosticLocationMaxUses,
            $"generator may use {DiagnosticLocationToken} at most {DiagnosticLocationMaxUses} time(s), for its own diagnostics only; found {diagnosticLocations}. Interceptor locations must come from GetInterceptableLocation.");
        var missingOwners = DiagnosticLocationOwners.Where(owner => !generator.Contains(owner, StringComparison.Ordinal)).ToList();
        Gate.Require(diagnosticLocations == 0 || missingOwners.Count == 0,
            () => $"generator uses {DiagnosticLocationToken} without reporting {ContractValue.Render(missingOwners)}; the only permitted uses are the qyl generator diagnostics");
        Gate.Require(!generator.Contains("InterceptsLocationAttribute(", StringComparison.Ordinal) || generator.Contains("GetInterceptsLocationAttributeSyntax(", StringComparison.Ordinal),
            "generator must emit InterceptsLocationAttribute through Roslyn GetInterceptsLocationAttributeSyntax");

        var interceptorKinds = VerifyInterceptorStructure(generator);
        var fixtureManifests = GeneratorManifests.ParseGeneratedInterceptorSource(File.ReadAllText(GeneratedInterceptorSnapshotPath), GeneratedInterceptorSnapshotPath);
        var coverageManifests = GeneratorManifests.ParseVerifiedManifestArtifact(File.ReadAllText(GeneratedInterceptorManifestArtifactPath), GeneratedInterceptorManifestArtifactPath);

        VerifyGeneratedInterceptorManifests(fixtureManifests, interceptorKinds, "two-DbCommand deterministic fixture");
        var fixtureKinds = fixtureManifests.Select(static manifest => ContractValue.Text(manifest["interceptorKind"])).ToList();
        Gate.Require(fixtureKinds.SequenceEqual(["DbCommand.Execute", "DbCommand.Execute"]), () => $"generated interceptor snapshot kind mismatch: {ContractValue.Render(fixtureKinds)}");
        var fixtureKeys = fixtureManifests.Select(static manifest => ContractModel.Strings(manifest["contractKeys"])).ToList();
        Gate.Require(fixtureKeys.Count == 2 && fixtureKeys.All(static keys => keys.SequenceEqual(["signals.traces.ADONET"])),
            () => $"generated interceptor snapshot contract-key mismatch: {ContractValue.Render(fixtureKeys.Select(static keys => (object?)keys.Cast<object?>().ToList()).ToList())}");

        var emittedKeys = VerifyGeneratedInterceptorManifests(coverageManifests, interceptorKinds, "verified live coverage");
        var emittedKinds = coverageManifests.Select(static manifest => ContractValue.Text(manifest["interceptorKind"])).ToHashSet(StringComparer.Ordinal);
        Gate.Require(emittedKinds.SetEquals(interceptorKinds),
            () => "live generated manifest kinds must exactly match the declared interceptor kinds: "
                  + $"missing={ContractValue.RenderSorted(interceptorKinds.Except(emittedKinds))} unexpected={ContractValue.RenderSorted(emittedKinds.Except(interceptorKinds))}");
        var unsupportedEmitted = emittedKeys.Intersect(unsupportedKeys).ToArray();
        Gate.Require(unsupportedEmitted.Length == 0, () => $"unsupported keys leaked into generated interceptor manifests: {ContractValue.RenderSorted(unsupportedEmitted)}");
        var unknownEmitted = emittedKeys.Except(implementedKeys).ToArray();
        Gate.Require(unknownEmitted.Length == 0, () => $"generated interceptor manifests contain non-implemented keys: {ContractValue.RenderSorted(unknownEmitted)}");
        var nonSource = emittedKeys.Except(sourceInterceptorKeys).ToHashSet(StringComparer.Ordinal);
        var unexpectedNonSource = nonSource.ToArray();
        Gate.Require(unexpectedNonSource.Length == 0, () => $"generated interceptor manifests contain keys not owned by the source_interceptor lane: {ContractValue.RenderSorted(unexpectedNonSource)}");
        var missingSource = sourceInterceptorKeys.Except(emittedKeys).ToArray();
        Gate.Require(missingSource.Length == 0, () => $"source_interceptor contract keys missing from live generated manifests: {ContractValue.RenderSorted(missingSource)}");
        Gate.Require(coverageManifests.Any(static manifest => ContractModel.Strings(manifest["additionalMetricIds"]).Length > 0),
            "live generated interceptor manifests do not exercise AdditionalMetricIds");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0; index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [GeneratedRegex(@"public const string ([A-Za-z0-9]+) = ""([A-Z0-9]+)"";")]
    private static partial Regex InstrumentationIdConstant();

    [GeneratedRegex("public static class (QylIntercepted[A-Za-z0-9]+)")]
    private static partial Regex InterceptedClass();

    [GeneratedRegex(@"^\s*QylAutoInstrumentationIds\.([A-Za-z0-9]+)")]
    private static partial Regex IntegrationHead();

    [GeneratedRegex(@"Signal\s*=\s*QylAutoInstrumentationSignal\.([A-Za-z]+)")]
    private static partial Regex IntegrationSignal();

    [GeneratedRegex(@"MetricIds\s*=\s*\[([^\]]*)\]")]
    private static partial Regex IntegrationMetricIds();

    [GeneratedRegex(@"QylAutoInstrumentationIds\.([A-Za-z0-9]+)")]
    private static partial Regex MetricIdConstant();

    [GeneratedRegex(@"public static [^\n(]+ ([A-Za-z0-9]+)(?:<[^>]+>)?\(")]
    private static partial Regex PublicStaticMethod();

    [GeneratedRegex(@"Shape\s*=\s*QylShapes\.([A-Za-z0-9]+)")]
    private static partial Regex InterceptShape();

    [GeneratedRegex(@"Body\s*=\s*QylInterceptorBody\.([A-Za-z]+)")]
    private static partial Regex InterceptBody();

    [GeneratedRegex(@"^\s*QylAutoInstrumentationIds\.([A-Za-z0-9]+),\s*QylAutoInstrumentationSignal\.([A-Za-z]+)\s*$")]
    private static partial Regex SignalDeclaration();

    [GeneratedRegex(@"const string ([A-Za-z0-9_]+) = ""([^""]*)"";")]
    private static partial Regex StringConstant();

    [GeneratedRegex(@"const string ([A-Za-z0-9_]+) = QylTelemetryNames\.Scopes\.([A-Za-z0-9_]+);")]
    private static partial Regex ScopeConstant();

    [GeneratedRegex(@"<Project Path=""([^""]+\.csproj)""")]
    private static partial Regex DemoSolutionProject();

    [GeneratedRegex(@"MetricMeterTable\s*=\s*\[(.*?)\n    \];", RegexOptions.Singleline)]
    private static partial Regex MeterTable();

    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"Meter\.Create(?:Histogram|Counter|UpDownCounter|Observable(?:Gauge|Counter|UpDownCounter))<[^>]+>\(\s*(?<name>[^,]+),\s*(?<unit>[^,)]+)")]
    private static partial Regex MetricInstrumentFactory();

    [GeneratedRegex(@"internal string\[\] ([A-Za-z0-9_]+)")]
    private static partial Regex InternalStringArrayMember();

    [GeneratedRegex(@"\bcatch\b")]
    private static partial Regex CatchKeyword();

    [GeneratedRegex(@"builder\.(?:Append|AppendLine)\((?<argument>.*?)\);", RegexOptions.Singleline)]
    private static partial Regex GeneratorEmission();

    [GeneratedRegex(@"^\s*([A-Za-z0-9]+),\s*$", RegexOptions.Multiline)]
    private static partial Regex EnumMember();

    [GeneratedRegex(@"public const string ([A-Za-z0-9]+) = ""([A-Za-z0-9]+)"";")]
    private static partial Regex ShapeConstant();

    [GeneratedRegex(@"case ""([A-Za-z0-9]+)"":")]
    private static partial Regex ShapeCase();
}
