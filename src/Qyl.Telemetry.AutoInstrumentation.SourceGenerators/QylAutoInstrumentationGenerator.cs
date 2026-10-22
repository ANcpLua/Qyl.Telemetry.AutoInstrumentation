using ANcpLua.Roslyn.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

/// <summary>
/// Emits the qyl source-level auto-instrumentation interceptors used by NativeAOT consumers.
/// </summary>
/// <remarks>
/// The generator runs in the compiler, reads the integration declarations carried by the referenced
/// qyl runtime assembly, discovers the source-visible invocation expressions they match, obtains
/// Roslyn <c>InterceptableLocation</c> data, and emits ordinary C# interceptor methods. Runtime
/// instrumentation stays in public qyl helper APIs; the generator never emits profiler, startup
/// hook, reflection, or runtime IL-rewrite code.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed partial class QylAutoInstrumentationGenerator : IIncrementalGenerator
{
    private const string ReceiverName = "receiver";
    private const string SharedHelperType = "global::Qyl.Telemetry.AutoInstrumentation.GeneratedCode.QylInterceptedActivity";

    private static readonly SymbolDisplayFormat FullyQualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions &
            ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // Shape matching ignores nullable reference annotations; emitted signatures must preserve them.
    private static readonly SymbolDisplayFormat EmittedTypeFormat = FullyQualifiedFormat.WithMiscellaneousOptions(
        FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // Runtime packages are excluded so QylIntercepted* helpers cannot self-intercept: an interceptor
    // is handed the receiver it reports on (QylInterceptedDbCommand.Execute takes the DbCommand whose
    // ExecuteReader is intercepted), so a call back onto that receiver from inside a runtime package
    // would re-enter the interceptor. Keep this set aligned with runtime packages under /src;
    // consumers, demos, and test fixtures remain instrumented.
    private static readonly HashSet<string> QylRuntimeAssemblies = new(StringComparer.Ordinal)
    {
        RuntimeAssemblyName,
        "Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners",
        "Qyl.Telemetry.AutoInstrumentation.Hosting",
        "Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore",
        "Qyl.Telemetry.AutoInstrumentation.SqlClient",
    };

    private readonly record struct ParameterSpec(string TypeName, string MatchTypeName, string Name, bool IsParams = false, RefKind RefKind = RefKind.None);

    private readonly record struct InterceptorTarget(
        IntegrationDeclaration Integration,
        InterceptDeclaration Intercept,
        string InstrumentationId,
        EquatableArray<string> AdditionalMetricIds,
        string ReceiverType,
        string MethodName,
        string ReturnType,
        EquatableArray<ParameterSpec> Parameters,
        bool IsAsync,
        string TypeParameterList,
        string ConstraintClauses,
        string ExtensionContainingType,
        string ShapeExpression,
        string ObsoleteWarningId);

    private readonly record struct InterceptedInvocation(InterceptorTarget Target, InterceptableLocation Location);

    // A call site whose receiver type and method name match a declared integration, but whose
    // signature does not fit the declared shape. No interceptor is emitted; QYL1001 is reported so
    // the lost instrumentation is visible instead of silent.
    private readonly record struct ShapeMismatch(
        string ReceiverType,
        string MethodName,
        string Shape,
        Location Location);

    // Exactly one of the two is set, or neither when the call site matches no declaration at all.
    private readonly record struct InvocationScanResult(
        InterceptedInvocation? Intercepted,
        ShapeMismatch? Mismatch);

    /// <summary>
    /// Registers the incremental syntax pipeline.
    /// </summary>
    /// <param name="context">Roslyn initialization context supplied by the compiler host.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interceptedInvocations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (syntaxContext, cancellationToken) =>
                    ScanInvocation(syntaxContext, cancellationToken))
            .Where(static result => result.Intercepted is not null || result.Mismatch is not null)
            .Collect();

        context.RegisterSourceOutput(
            interceptedInvocations,
            static (productionContext, invocations) =>
                EmitInterceptors(productionContext, invocations));

        // QYL1002: the libraries whose telemetry the consumer turns on in their own code, by a call or
        // by constructing the library's telemetry wrapper.
        var optInSignals = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax,
                static (syntaxContext, cancellationToken) => ScanOptIn(syntaxContext, cancellationToken))
            .Where(static signal => signal.Row is not 0)
            .Collect();

        context.RegisterSourceOutput(
            optInSignals,
            static (productionContext, signals) => ReportMissingOptIns(productionContext, signals));

        // QylConsumerBuild: an executable's own version and revision as constants, AddQyl's default
        // service.version.
        var consumerBuild = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider.Select(static (compilation, _) => IsConsumerExecutable(compilation)))
            .Select(static (pair, _) => pair.Right ? ReadConsumerBuild(pair.Left.GlobalOptions) : null);

        context.RegisterSourceOutput(
            consumerBuild,
            static (productionContext, build) => EmitConsumerBuild(productionContext, build));

        var declaredSignals = context.CompilationProvider
            .Select(static (compilation, cancellationToken) => ReadDeclaredSignals(compilation, cancellationToken));

        context.RegisterSourceOutput(
            declaredSignals,
            static (productionContext, signals) =>
                EmitDeclaredInstrumentations(productionContext, signals));
    }

    private static InvocationScanResult ScanInvocation(
        GeneratorSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var compilation = context.SemanticModel.Compilation;
        if (compilation.AssemblyName is { } assemblyName &&
            QylRuntimeAssemblies.Contains(assemblyName))
        {
            return default;
        }

        var catalog = GetCatalog(compilation);
        if (catalog.Integrations.IsEmpty)
            return default;

        if (context.SemanticModel.GetInterceptorMethod(invocation, cancellationToken) is not null)
            return default;

        if (context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol symbol)
            return default;

        var receiverType = GetInvocationReceiverType(invocation, context.SemanticModel, cancellationToken);
        if (!TryGetInvocation(catalog, symbol, receiverType, out var target, out var declaredShape))
        {
            // The receiver and method named a declared integration but the signature did not fit its
            // shape. Report it rather than dropping the call site silently; see QYL1001.
            return declaredShape.Length is 0
                ? default
                : new InvocationScanResult(
                    null,
                    new ShapeMismatch(
                        symbol.ContainingType.ToDisplayString(FullyQualifiedFormat),
                        symbol.Name,
                        declaredShape,
                        invocation.GetLocation()));
        }

        if (context.SemanticModel.GetInterceptableLocation(invocation, cancellationToken) is not { } interceptableLocation)
            return default;

        return new InvocationScanResult(new InterceptedInvocation(target, interceptableLocation), null);
    }

    private static ITypeSymbol? GetInvocationReceiverType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
        => invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type
            : null;

    private static bool TryGetInvocation(
        DeclarationCatalog catalog,
        IMethodSymbol symbol,
        ITypeSymbol? receiverType,
        out InterceptorTarget target,
        out string unmatchedShape)
    {
        unmatchedShape = string.Empty;
        foreach (var integration in catalog.Integrations)
        {
            foreach (var intercept in integration.Intercepts)
            {
                if (TryMatchDeclaration(integration, intercept, symbol, receiverType, out target, out var declaredShape))
                    return true;

                // Remember the first declaration whose receiver and method matched but whose shape did
                // not; a later declaration may still match outright, in which case this is discarded.
                if (declaredShape.Length > 0 && unmatchedShape.Length is 0)
                    unmatchedShape = declaredShape;
            }
        }

        target = default;
        return false;
    }

    private static bool TryMatchDeclaration(
        IntegrationDeclaration integration,
        InterceptDeclaration intercept,
        IMethodSymbol symbol,
        ITypeSymbol? receiverType,
        out InterceptorTarget target,
        out string unmatchedShape)
    {
        target = default;
        unmatchedShape = string.Empty;
        if (intercept.Methods.Length > 0 && !intercept.Methods.Contains(symbol.Name))
            return false;

        if (!MatchesReceiver(intercept.ReceiverType, symbol))
            return false;

        // The library owns a native ActivitySource qyl subscribes, so the call is not this lane's to
        // intercept. It is not a shape mismatch either: unmatchedShape stays empty and no QYL1001 is
        // reported for a call site qyl deliberately leaves to the library's own span.
        if (IsNativeSourceReceiver(intercept, receiverType ?? symbol.ContainingType))
            return false;

        if (!TryMatchShape(intercept.Shape, integration, symbol, receiverType, out var shape))
        {
            // Only worth reporting when the declaration actually named this API. A declaration with no
            // receiver type or no method list matches every invocation in the compilation and leaves the
            // narrowing to the shape function, so a shape miss there says nothing about consumer intent
            // -- reporting it would fire QYL1001 on Task.FromResult and int.ToString.
            if (intercept.ReceiverType.Length > 0 && intercept.Methods.Length > 0)
                unmatchedShape = intercept.Shape;

            return false;
        }

        var overridesId = shape.InstrumentationId.Length > 0;
        target = new InterceptorTarget(
            integration,
            intercept,
            overridesId ? shape.InstrumentationId : integration.InstrumentationId,
            overridesId ? shape.MetricIds : integration.MetricIds,
            shape.ReceiverTypeName,
            symbol.Name,
            shape.ReturnType,
            shape.Parameters,
            shape.IsAsync,
            shape.TypeParameterList,
            shape.ConstraintClauses,
            shape.ExtensionContainingType,
            shape.ShapeExpression,
            GetObsoleteWarningId(symbol));
        return true;
    }

    private static bool IsNativeSourceReceiver(InterceptDeclaration intercept, ITypeSymbol receiverType)
    {
        if (intercept.NativeSourceReceivers.Length is 0)
            return false;

        if (receiverType.ContainingNamespace is not { } containingNamespace)
            return false;

        var namespaceName = containingNamespace.ToDisplayString();
        foreach (var declared in intercept.NativeSourceReceivers)
        {
            if (NamespaceMatches(namespaceName, declared))
                return true;
        }

        return false;
    }

    private static bool MatchesReceiver(string declaredReceiverType, IMethodSymbol symbol)
    {
        if (declaredReceiverType.Length is 0)
            return true;

        var separator = declaredReceiverType.LastIndexOf('.');
        var namespaceName = separator < 0 ? string.Empty : declaredReceiverType.Substring(0, separator);
        var metadataName = declaredReceiverType.Substring(separator + 1);
        if (IsOrDerivesOrImplements(symbol.ContainingType, namespaceName, metadataName))
            return true;

        return symbol.ReducedFrom is { Parameters.Length: > 0 } original &&
               IsOrDerivesOrImplements(original.Parameters[0].Type, namespaceName, metadataName);
    }

    private static void EmitInterceptors(
        SourceProductionContext context,
        ImmutableArray<InvocationScanResult> results)
    {
        if (results.IsDefaultOrEmpty)
            return;

        // Report every call site that named a declared integration but did not fit its shape, in a
        // stable order so the build log is reproducible. These produce no interceptor by design.
        var mismatches = results
            .Where(static result => result.Mismatch is not null)
            .Select(static result => result.Mismatch!.Value)
            .Distinct()
            .OrderBy(static mismatch => mismatch.Location.GetLineSpan().Path, StringComparer.Ordinal)
            .ThenBy(static mismatch => mismatch.Location.SourceSpan.Start)
            .ThenBy(static mismatch => mismatch.ReceiverType, StringComparer.Ordinal)
            .ThenBy(static mismatch => mismatch.MethodName, StringComparer.Ordinal);

        foreach (var mismatch in mismatches)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                QylGeneratorDiagnostics.ShapeNotMatched,
                mismatch.Location,
                mismatch.ReceiverType,
                mismatch.MethodName,
                mismatch.Shape));
        }

        var invocations = results
            .Where(static result => result.Intercepted is not null)
            .Select(static result => result.Intercepted!.Value)
            .Distinct()
            // Stable, content-based ordering so the emission order and the _N interceptor-name indices
            // are a pure function of the matched call sites — independent of Roslyn's cross-tree syntax
            // visitation order, which is NOT guaranteed stable across machines or incremental rebuilds.
            // Location.Data encodes file path + position and is unique per call site; the Target tie-break
            // is belt-and-suspenders. Keeps the generated file byte-reproducible (Directory.Build.props
            // sets Deterministic=true) once a consumer has 2+ matched call sites.
            .OrderBy(static invocation => invocation.Location.Data, StringComparer.Ordinal)
            .ThenBy(static invocation => invocation.Target.ReceiverType, StringComparer.Ordinal)
            .ThenBy(static invocation => invocation.Target.MethodName, StringComparer.Ordinal)
            .ToArray();

        if (invocations.Length is 0)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        EmitInterceptsLocationAttribute(builder);
        builder.AppendLine("namespace Qyl.Telemetry.AutoInstrumentation.Generated");
        builder.AppendLine("{");
        builder.AppendLine("    internal static class QylGeneratedInterceptors");
        builder.AppendLine("    {");
        builder.AppendLine("        private const int RequiredQylGeneratedCodeAbi = global::Qyl.Telemetry.AutoInstrumentation.GeneratedCode.QylGeneratedCodeAbi.V28;");
        builder.AppendLine();

        for (var index = 0; index < invocations.Length; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var invocation = invocations[index];
            var target = invocation.Target;
            EmitInterceptorManifest(builder, in target);
            EmitInterceptorBody(builder, in invocation, index);
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("QylAutoInstrumentation.Interceptors.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void EmitInterceptorBody(StringBuilder builder, in InterceptedInvocation invocation, int index)
    {
        switch (invocation.Target.Intercept.Body)
        {
            case InterceptorBody.Trace:
                EmitTraceInterceptor(builder, in invocation, index);
                return;
            case InterceptorBody.DbCommand:
                EmitDbCommandInterceptor(builder, in invocation, index);
                return;
            default:
                throw new InvalidOperationException("Unknown interceptor body: " + invocation.Target.Intercept.Body);
        }
    }

    private static void EmitInterceptorManifest(StringBuilder builder, in InterceptorTarget target)
    {
        builder.Append("        // qyl-interceptor-manifest: {\"interceptorKind\":");
        AppendStringLiteral(builder, target.Intercept.Kind);
        builder.Append(",\"signal\":");
        AppendStringLiteral(builder, GetManifestSignalName(target.Integration.Signal));
        builder.Append(",\"instrumentationId\":");
        AppendStringLiteral(builder, target.InstrumentationId);
        builder.Append(",\"additionalMetricIds\":[");
        for (var index = 0; index < target.AdditionalMetricIds.Length; index++)
        {
            if (index > 0)
                builder.Append(',');
            AppendStringLiteral(builder, target.AdditionalMetricIds[index]);
        }

        builder.Append("],\"contractKeys\":[");
        AppendStringLiteral(builder, GetContractKey(target.Integration.Signal, target.InstrumentationId));
        foreach (var metricId in target.AdditionalMetricIds)
        {
            builder.Append(',');
            AppendStringLiteral(builder, GetContractKey(TelemetrySignal.Metrics, metricId));
        }

        builder.AppendLine("]}");
    }

    private static string GetContractKey(TelemetrySignal signal, string instrumentationId)
        => "signals." + GetManifestSignalName(signal) + "." + instrumentationId;

    private static string GetManifestSignalName(TelemetrySignal signal)
        => signal switch
        {
            TelemetrySignal.Traces => "traces",
            TelemetrySignal.Metrics => "metrics",
            TelemetrySignal.Logs => "logs",
            _ => throw new InvalidOperationException("Unknown telemetry signal: " + signal),
        };

    private static void EmitInterceptsLocationAttribute(StringBuilder builder)
    {
        builder.AppendLine("namespace System.Runtime.CompilerServices");
        builder.AppendLine("{");
        builder.AppendLine(
            "    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        builder.AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute");
        builder.AppendLine("    {");
        builder.AppendLine("        public InterceptsLocationAttribute(int version, string data)");
        builder.AppendLine("        {");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
    }

    private static void EmitDeclaredInstrumentations(SourceProductionContext context, DeclaredSignals? signals)
    {
        if (signals is null)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("namespace Qyl.Telemetry.AutoInstrumentation");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>The instrumentation ids each signal's environment toggles bind to, read from the runtime's integration and signal declarations.</summary>");
        builder.AppendLine("    internal static class QylDeclaredInstrumentations");
        builder.AppendLine("    {");
        AppendDeclaredIdArray(builder, "Traces", signals.Traces);
        builder.AppendLine();
        AppendDeclaredIdArray(builder, "Metrics", signals.Metrics);
        builder.AppendLine();
        AppendDeclaredIdArray(builder, "Logs", signals.Logs);
        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("QylDeclaredInstrumentations.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void AppendDeclaredIdArray(StringBuilder builder, string name, EquatableArray<string> instrumentationIds)
    {
        builder.Append("        public static readonly string[] ");
        builder.Append(name);
        builder.AppendLine(" =");
        builder.AppendLine("        [");
        foreach (var instrumentationId in instrumentationIds)
        {
            builder.Append("            ");
            AppendStringLiteral(builder, instrumentationId);
            builder.AppendLine(",");
        }

        builder.AppendLine("        ];");
    }

    private static string GetMethodPrefix(in InterceptorTarget target)
        => target.Integration.Name + "_" + target.MethodName;

    private static void EmitFailureAndDisposal(StringBuilder builder, in InterceptorTarget target, bool recordsDuration, bool runtimeObservesTask)
    {
        builder.AppendLine("            }");
        builder.AppendLine("            catch (global::System.Exception exception)");
        builder.AppendLine("            {");
        builder.Append("                ");
        builder.Append(SharedHelperType);
        builder.AppendLine(".RecordFailure(activity, exception);");
        if (recordsDuration)
            AppendRecordDurationStatement(builder, in target);
        if (runtimeObservesTask)
            builder.AppendLine("                activity?.Dispose();");
        builder.AppendLine("                throw;");
        builder.AppendLine("            }");
        if (runtimeObservesTask)
            return;

        builder.AppendLine("            finally");
        builder.AppendLine("            {");
        builder.AppendLine("                activity?.Dispose();");
        builder.AppendLine("            }");
    }

    private static bool RuntimeObservesTask(in InterceptorTarget target)
        => target.Intercept.ObserveAsync &&
           target.IsAsync &&
           (!target.Intercept.ObserveByRefOnly || HasByRefParameters(target.Parameters));

    private static void EmitTraceInterceptor(StringBuilder builder, in InterceptedInvocation invocation, int index)
    {
        var target = invocation.Target;
        var intercept = target.Intercept;
        var helperType = target.Integration.HelperType;
        var runtimeObservesAsync = RuntimeObservesTask(in target);
        var signatureIsAsync = target.IsAsync && !runtimeObservesAsync;
        EmitAttributeAndSignature(
            builder,
            invocation.Location,
            target.ReturnType,
            GetMethodPrefix(in target),
            index,
            target.ReceiverType,
            target.Parameters,
            signatureIsAsync,
            target.TypeParameterList,
            target.ConstraintClauses);
        builder.AppendLine("        {");
        if (intercept.Metric.Length > 0)
        {
            builder.Append("            var metricStart = ");
            builder.Append(helperType);
            builder.AppendLine(".GetTimestamp();");
        }

        builder.Append("            var activity = ");
        AppendHelperCall(builder, helperType, intercept.Start, intercept.StartParameters, in target, string.Empty);
        builder.AppendLine(";");
        if (intercept.Enrich.Length > 0)
        {
            builder.AppendLine("            if (activity is not null)");
            builder.AppendLine("            {");
            builder.Append("                ");
            AppendHelperCall(builder, helperType, intercept.Enrich, intercept.EnrichParameters, in target, "activity");
            builder.AppendLine(";");
            builder.AppendLine("            }");
        }

        builder.AppendLine("            try");
        builder.AppendLine("            {");
        EmitTraceInvocation(builder, in target, runtimeObservesAsync);
        EmitFailureAndDisposal(builder, in target, recordsDuration: intercept.Metric.Length > 0, runtimeObservesAsync);
        builder.AppendLine("        }");
        builder.AppendLine();
    }

    private static void EmitTraceInvocation(StringBuilder builder, in InterceptorTarget target, bool runtimeObservesAsync)
    {
        if (runtimeObservesAsync)
        {
            AppendForwardedInvocationStatement(builder, in target, "var resultTask = ");
            builder.Append("                return ");
            builder.Append(SharedHelperType);
            builder.AppendLine(".ObserveAsync(resultTask, activity);");
            return;
        }

        if (target.IsAsync)
        {
            if (IsTaskLikeReturnWithoutResult(target.ReturnType))
            {
                AppendForwardedInvocationStatement(builder, in target, "await ", ".ConfigureAwait(false);");
                EmitTraceSuccessDurationMetric(builder, in target);
                return;
            }

            AppendForwardedInvocationStatement(builder, in target, "var result = await ", ".ConfigureAwait(false);");
            EmitTraceSuccessDurationMetric(builder, in target);
            builder.AppendLine("                return result;");
            return;
        }

        if (string.Equals(target.ReturnType, "void", StringComparison.Ordinal))
        {
            AppendForwardedInvocationStatement(builder, in target, string.Empty);
            EmitTraceSuccessDurationMetric(builder, in target);
            return;
        }

        AppendForwardedInvocationStatement(builder, in target, "var result = ");
        EmitTraceSuccessDurationMetric(builder, in target);
        builder.AppendLine("                return result;");
    }

    private static void EmitTraceSuccessDurationMetric(StringBuilder builder, in InterceptorTarget target)
    {
        if (target.Intercept.Metric.Length > 0)
            AppendRecordDurationStatement(builder, in target);
    }

    private static void AppendRecordDurationStatement(StringBuilder builder, in InterceptorTarget target)
    {
        builder.Append("                ");
        AppendHelperCall(
            builder,
            target.Integration.HelperType,
            target.Intercept.Metric,
            target.Intercept.MetricParameters,
            in target,
            "metricStart");
        builder.AppendLine(";");
    }

    private static bool IsTaskLikeReturnWithoutResult(string returnType)
        => returnType is "global::System.Threading.Tasks.Task" or
            "global::System.Threading.Tasks.ValueTask";

    private static void AppendHelperCall(
        StringBuilder builder,
        string helperType,
        string method,
        EquatableArray<BoundParameter> parameters,
        in InterceptorTarget target,
        string leadingArgument)
    {
        builder.Append(helperType);
        builder.Append('.');
        builder.Append(method);
        builder.Append('(');
        var first = true;
        if (leadingArgument.Length > 0)
        {
            builder.Append(leadingArgument);
            first = false;
        }

        foreach (var parameter in parameters)
        {
            if (!first)
                builder.Append(", ");
            first = false;
            AppendBoundArgument(builder, in parameter, in target);
        }

        builder.Append(')');
    }

    private static void AppendBoundArgument(StringBuilder builder, in BoundParameter parameter, in InterceptorTarget target)
    {
        foreach (var binding in parameter.Bindings)
        {
            switch (binding.Source)
            {
                case BindingSource.Argument:
                    if (binding.Index < target.Parameters.Length &&
                        (binding.TypeName.Length is 0 || TypeNameMatches(binding.TypeName, target.Parameters[binding.Index].MatchTypeName)))
                    {
                        builder.Append(binding.Convert.Replace("{0}", target.Parameters[binding.Index].Name));
                        return;
                    }

                    continue;
                case BindingSource.Receiver:
                    builder.Append(ReceiverName);
                    if (binding.Convert.Length > 0)
                    {
                        builder.Append('.');
                        builder.Append(binding.Convert);
                    }

                    return;
                case BindingSource.InstrumentationId:
                    AppendStringLiteral(builder, target.InstrumentationId);
                    return;
                case BindingSource.Shape:
                    builder.Append(target.ShapeExpression.Length > 0 ? target.ShapeExpression : "null");
                    return;
                default:
                    throw new InvalidOperationException("Unknown argument binding source: " + binding.Source);
            }
        }

        builder.Append("default");
    }

    private static bool TypeNameMatches(string declared, string actual)
        => string.Equals(NormalizeTypeName(declared), NormalizeTypeName(actual), StringComparison.Ordinal);

    private static string NormalizeTypeName(string typeName)
    {
        var stripped = typeName.Replace("global::", string.Empty);
        return stripped switch
        {
            "string" => "System.String",
            "object" => "System.Object",
            "int" => "System.Int32",
            "long" => "System.Int64",
            "double" => "System.Double",
            "bool" => "System.Boolean",
            "byte[]" => "System.Byte[]",
            _ => stripped,
        };
    }

    private static void EmitDbCommandInterceptor(StringBuilder builder, in InterceptedInvocation invocation, int index)
    {
        var target = invocation.Target;
        var helperType = target.Integration.HelperType;
        EmitAttributeAndSignature(
            builder,
            invocation.Location,
            target.ReturnType,
            GetMethodPrefix(in target),
            index,
            target.ReceiverType,
            target.Parameters,
            isAsync: false,
            string.Empty,
            string.Empty);
        builder.AppendLine("        {");
        builder.Append("            var metricStart = ");
        builder.Append(helperType);
        builder.AppendLine(".GetTimestamp();");
        builder.Append("            var activity = ");
        AppendHelperCall(builder, helperType, target.Intercept.Start, target.Intercept.StartParameters, in target, string.Empty);
        builder.AppendLine(";");
        builder.AppendLine("            try");
        builder.AppendLine("            {");

        if (target.IsAsync)
        {
            AppendForwardedInvocationStatement(builder, in target, "var resultTask = ");
            builder.Append("                return ");
            builder.Append(helperType);
            builder.Append(".ObserveAsync(resultTask, activity, metricStart, ");
            AppendStringLiteral(builder, target.InstrumentationId);
            builder.AppendLine(");");
        }
        else
        {
            AppendForwardedInvocationStatement(builder, in target, "var result = ");
            AppendRecordDurationStatement(builder, in target);
            builder.AppendLine("                return result;");
        }

        EmitFailureAndDisposal(builder, in target, recordsDuration: true, runtimeObservesTask: target.IsAsync);
        builder.AppendLine("        }");
        builder.AppendLine();
    }

    private static void EmitAttributeAndSignature(
        StringBuilder builder,
        InterceptableLocation location,
        string returnType,
        string methodPrefix,
        int index,
        string receiverType,
        EquatableArray<ParameterSpec> parameters,
        bool isAsync,
        string typeParameterList,
        string constraintClauses)
    {
        var attribute = location.GetInterceptsLocationAttributeSyntax();
        var displayLocation = location.GetDisplayLocation();
        builder.Append("        // Intercepted call at ");
        builder.AppendLine(displayLocation);
        builder.Append("        ");
        builder.AppendLine(attribute);
        builder.Append("        public static ");
        if (isAsync)
            builder.Append("async ");

        builder.Append(returnType);
        builder.Append(' ');
        builder.Append(methodPrefix);
        builder.Append('_');
        builder.Append(index.ToString(CultureInfo.InvariantCulture));
        builder.Append(typeParameterList);
        builder.Append("(this ");
        builder.Append(receiverType);
        builder.Append(' ');
        builder.Append(ReceiverName);
        AppendParameterList(builder, parameters);
        builder.Append(')');
        builder.Append(constraintClauses);
        builder.AppendLine();
    }

    private static void AppendParameterList(StringBuilder builder, EquatableArray<ParameterSpec> parameters)
    {
        foreach (var parameter in parameters)
        {
            builder.Append(", ");
            if (parameter.IsParams)
                builder.Append("params ");

            if (parameter.RefKind is RefKind.In)
                builder.Append("in ");

            builder.Append(parameter.TypeName);
            builder.Append(' ');
            builder.Append(parameter.Name);
        }
    }

    private static void AppendArgumentList(StringBuilder builder, EquatableArray<ParameterSpec> parameters,
        bool includeLeadingComma)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0 || includeLeadingComma)
                builder.Append(", ");

            if (parameters[i].RefKind is RefKind.In)
                builder.Append("in ");

            builder.Append(parameters[i].Name);
        }
    }

    private static bool HasByRefParameters(EquatableArray<ParameterSpec> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.RefKind is not RefKind.None)
                return true;
        }

        return false;
    }

    private static string GetObsoleteWarningId(IMethodSymbol method)
    {
        var obsolete = method.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute");
        if (obsolete is null || obsolete.GetConstructorArgument<bool>(1) is true)
            return string.Empty;

        var diagnosticId = obsolete.GetNamedArgument<string>("DiagnosticId");
        if (!string.IsNullOrEmpty(diagnosticId))
        {
            // Only pragma-addressable IDs can be scoped to a forwarding statement.
            return SyntaxFacts.IsValidIdentifier(diagnosticId) ? diagnosticId : string.Empty;
        }

        return obsolete.GetConstructorArgument<string>(0) is null ? "CS0612" : "CS0618";
    }

    private static void AppendForwardedInvocationStatement(StringBuilder builder, in InterceptorTarget target,
        string prefix, string suffix = ";")
    {
        if (target.ObsoleteWarningId.Length > 0)
            builder.Append("#pragma warning disable ").Append(target.ObsoleteWarningId)
                .AppendLine(" // The original call site owns deprecation diagnostics.");

        builder.Append("                ").Append(prefix);
        AppendInvocationCall(builder, in target);
        builder.AppendLine(suffix);

        if (target.ObsoleteWarningId.Length > 0)
            builder.Append("#pragma warning restore ").AppendLine(target.ObsoleteWarningId);
    }

    private static void AppendInvocationCall(StringBuilder builder, in InterceptorTarget target)
    {
        if (!string.IsNullOrEmpty(target.ExtensionContainingType))
        {
            builder.Append(target.ExtensionContainingType);
            builder.Append('.');
            builder.Append(target.MethodName);
            builder.Append(target.TypeParameterList);
            builder.Append('(');
            builder.Append(ReceiverName);
            AppendArgumentList(builder, target.Parameters, includeLeadingComma: true);
            builder.Append(')');
            return;
        }

        builder.Append(ReceiverName);
        builder.Append('.');
        builder.Append(target.MethodName);
        builder.Append(target.TypeParameterList);
        builder.Append('(');
        AppendArgumentList(builder, target.Parameters, includeLeadingComma: false);
        builder.Append(')');
    }
}
