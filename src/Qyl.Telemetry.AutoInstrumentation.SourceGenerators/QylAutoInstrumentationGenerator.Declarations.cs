using ANcpLua.Roslyn.Utilities;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

public sealed partial class QylAutoInstrumentationGenerator
{
    private const string RuntimeAssemblyName = "Qyl.Telemetry.AutoInstrumentation";
    private const string HelperTypePrefix = "QylIntercepted";
    private const string IntegrationAttributeName = "QylIntegrationAttribute";
    private const string InterceptAttributeName = "QylInterceptAttribute";
    private const string SignalAttributeName = "QylSignalAttribute";
    private static readonly string[] GeneratedCodeNamespace = ["Qyl", "Telemetry", "AutoInstrumentation", "GeneratedCode"];

    /// <summary>Mirrors <c>QylInterceptorBody</c> in the runtime; read from metadata by value.</summary>
    private enum InterceptorBody
    {
        Trace,
        DbCommand,
    }

    private enum BindingSource
    {
        Argument,
        Receiver,
        InstrumentationId,
        Shape,
    }

    private enum TelemetrySignal
    {
        Traces,
        Metrics,
        Logs,
    }

    /// <summary>One way a helper parameter can be bound; <paramref name="Convert"/> is the argument format for <see cref="BindingSource.Argument"/> and the member path for <see cref="BindingSource.Receiver"/>.</summary>
    private readonly record struct ArgumentBinding(BindingSource Source, int Index, string TypeName, string Convert);

    /// <summary>A helper parameter's candidate bindings, tried in declaration order; none matching binds <c>default</c>.</summary>
    private readonly record struct BoundParameter(EquatableArray<ArgumentBinding> Bindings);

    private sealed record InterceptDeclaration(
        string Kind,
        string ReceiverType,
        EquatableArray<string> Methods,
        string Shape,
        InterceptorBody Body,
        string Start,
        EquatableArray<BoundParameter> StartParameters,
        bool ObserveAsync,
        bool ObserveByRefOnly,
        string Enrich,
        EquatableArray<BoundParameter> EnrichParameters,
        string Metric,
        EquatableArray<BoundParameter> MetricParameters,
        EquatableArray<string> NativeSourceReceivers);

    private sealed record IntegrationDeclaration(
        string Name,
        string HelperType,
        string InstrumentationId,
        TelemetrySignal Signal,
        EquatableArray<string> MetricIds,
        EquatableArray<InterceptDeclaration> Intercepts);

    private sealed record DeclaredSignals(
        EquatableArray<string> Traces,
        EquatableArray<string> Metrics,
        EquatableArray<string> Logs);

    private sealed class DeclarationCatalog(ImmutableArray<IntegrationDeclaration> integrations)
    {
        public static readonly DeclarationCatalog Empty = new(ImmutableArray<IntegrationDeclaration>.Empty);

        public ImmutableArray<IntegrationDeclaration> Integrations { get; } = integrations;
    }

    // One catalog per compilation: the referenced runtime assembly's declarations are read once and
    // reused by every invocation the syntax provider visits in that compilation.
    private static readonly ConditionalWeakTable<Compilation, DeclarationCatalog> Catalogs = new();

    private static DeclarationCatalog GetCatalog(Compilation compilation)
        => Catalogs.GetValue(compilation, static current => ReadCatalog(current));

    private static DeclarationCatalog ReadCatalog(Compilation compilation)
    {
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (string.Equals(assembly.Name, RuntimeAssemblyName, StringComparison.Ordinal))
                return new DeclarationCatalog(ReadIntegrations(assembly));
        }

        return DeclarationCatalog.Empty;
    }

    private static ImmutableArray<IntegrationDeclaration> ReadIntegrations(IAssemblySymbol assembly)
    {
        if (FindNamespace(assembly.GlobalNamespace, GeneratedCodeNamespace) is not { } generatedCode)
            return ImmutableArray<IntegrationDeclaration>.Empty;

        var builder = ImmutableArray.CreateBuilder<IntegrationDeclaration>();
        foreach (var type in generatedCode.GetTypeMembers())
        {
            foreach (var attribute in type.GetAttributes())
            {
                if (IsAttribute(attribute, IntegrationAttributeName))
                {
                    builder.Add(ReadIntegration(type, attribute));
                    break;
                }
            }
        }

        builder.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return builder.ToImmutable();
    }

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol root, string[] parts)
    {
        var current = root;
        foreach (var part in parts)
        {
            INamespaceSymbol? next = null;
            foreach (var member in current.GetNamespaceMembers())
            {
                if (string.Equals(member.Name, part, StringComparison.Ordinal))
                {
                    next = member;
                    break;
                }
            }

            if (next is null)
                return null;

            current = next;
        }

        return current;
    }

    private static IntegrationDeclaration ReadIntegration(INamedTypeSymbol type, AttributeData attribute)
    {
        var instrumentationId = attribute.GetConstructorArgument<string>(0) ?? string.Empty;
        var signal = (TelemetrySignal)attribute.GetNamedArgument<int>("Signal");
        var metricIds = attribute.GetNamedArgumentArray<string>("MetricIds");
        var name = type.Name.StartsWithOrdinal(HelperTypePrefix)
            ? type.Name.Substring(HelperTypePrefix.Length)
            : type.Name;
        var intercepts = ImmutableArray.CreateBuilder<InterceptDeclaration>();
        foreach (var candidate in type.GetAttributes())
        {
            if (IsAttribute(candidate, InterceptAttributeName))
                intercepts.Add(ReadIntercept(type, name, candidate));
        }

        return new IntegrationDeclaration(
            name,
            EmittedTypeName(type),
            instrumentationId,
            signal,
            metricIds.AsEquatableArray(),
            intercepts.ToImmutable().AsEquatableArray());
    }

    private static InterceptDeclaration ReadIntercept(INamedTypeSymbol type, string integrationName, AttributeData attribute)
    {
        var receiverType = attribute.GetConstructorArgument<string>(0) ?? string.Empty;
        var methods = attribute.GetConstructorArgumentArray<string>(1);
        var shape = attribute.GetNamedArgument<string>("Shape") ?? string.Empty;
        var start = attribute.GetNamedArgument<string>("Start") ?? string.Empty;
        var enrich = attribute.GetNamedArgument<string>("Enrich") ?? string.Empty;
        var metric = attribute.GetNamedArgument<string>("Metric") ?? string.Empty;
        var body = (InterceptorBody)attribute.GetNamedArgument<int>("Body");
        var observeAsync = attribute.GetNamedArgument<bool>("ObserveAsync");
        var observeByRefOnly = attribute.GetNamedArgument<bool>("ObserveByRefOnly");
        var nativeSourceReceivers = attribute.GetNamedArgumentArray<string>("NativeSourceReceivers");
        if (shape.Length is 0)
            throw new InvalidOperationException("Interceptor declaration on " + type.Name + " names no shape.");

        return new InterceptDeclaration(
            integrationName + "." + (start.Length > 0 ? start : body.ToString()),
            receiverType,
            methods.AsEquatableArray(),
            shape,
            body,
            start,
            ReadBoundParameters(type, start, skip: 0),
            observeAsync,
            observeByRefOnly,
            enrich,
            ReadBoundParameters(type, enrich, skip: 1),
            metric,
            ReadBoundParameters(type, metric, skip: 1),
            nativeSourceReceivers.AsEquatableArray());
    }

    private static EquatableArray<BoundParameter> ReadBoundParameters(INamedTypeSymbol type, string methodName, int skip)
    {
        if (methodName.Length is 0)
            return default;

        IMethodSymbol? method = null;
        foreach (var member in type.GetMembers(methodName))
        {
            if (member is IMethodSymbol candidate)
            {
                method = candidate;
                break;
            }
        }

        if (method is null)
            throw new InvalidOperationException("Interceptor declaration on " + type.Name + " names a missing helper method " + methodName + ".");

        var builder = ImmutableArray.CreateBuilder<BoundParameter>();
        for (var index = skip; index < method.Parameters.Length; index++)
            builder.Add(ReadBoundParameter(method.Parameters[index]));

        return builder.ToImmutable().AsEquatableArray();
    }

    private static BoundParameter ReadBoundParameter(IParameterSymbol parameter)
    {
        var bindings = ImmutableArray.CreateBuilder<ArgumentBinding>();
        foreach (var attribute in parameter.GetAttributes())
        {
            switch (attribute.AttributeClass?.Name)
            {
                case "QylFromArgumentAttribute":
                    bindings.Add(new ArgumentBinding(
                        BindingSource.Argument,
                        attribute.GetConstructorArgument<int>(0),
                        attribute.GetNamedArgument<string>("Type") ?? string.Empty,
                        attribute.GetNamedArgument<string>("Convert") ?? "{0}"));
                    break;
                case "QylFromReceiverAttribute":
                    bindings.Add(new ArgumentBinding(BindingSource.Receiver, 0, string.Empty, attribute.GetConstructorArgument<string>(0) ?? string.Empty));
                    break;
                case "QylFromInstrumentationIdAttribute":
                    bindings.Add(new ArgumentBinding(BindingSource.InstrumentationId, 0, string.Empty, string.Empty));
                    break;
                case "QylFromShapeAttribute":
                    bindings.Add(new ArgumentBinding(BindingSource.Shape, 0, string.Empty, string.Empty));
                    break;
            }
        }

        return new BoundParameter(bindings.ToImmutable().AsEquatableArray());
    }

    private static DeclaredSignals? ReadDeclaredSignals(Compilation compilation, CancellationToken cancellationToken)
    {
        if (!string.Equals(compilation.AssemblyName, RuntimeAssemblyName, StringComparison.Ordinal))
            return null;

        var traces = new SortedSet<string>(StringComparer.Ordinal);
        var metrics = new SortedSet<string>(StringComparer.Ordinal);
        var logs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var integration in ReadIntegrations(compilation.Assembly))
        {
            SignalSet(integration.Signal, traces, metrics, logs).Add(integration.InstrumentationId);
            foreach (var metricId in integration.MetricIds)
                metrics.Add(metricId);
        }

        CollectSignalDeclarations(compilation.Assembly.GlobalNamespace, traces, metrics, logs, cancellationToken);
        return new DeclaredSignals(
            traces.ToImmutableArray().AsEquatableArray(),
            metrics.ToImmutableArray().AsEquatableArray(),
            logs.ToImmutableArray().AsEquatableArray());
    }

    private static void CollectSignalDeclarations(
        INamespaceSymbol current,
        SortedSet<string> traces,
        SortedSet<string> metrics,
        SortedSet<string> logs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var type in current.GetTypeMembers())
        {
            foreach (var attribute in type.GetAttributes())
            {
                if (!IsAttribute(attribute, SignalAttributeName))
                    continue;

                var signal = (TelemetrySignal)attribute.GetConstructorArgument<int>(1);
                SignalSet(signal, traces, metrics, logs).Add(attribute.GetConstructorArgument<string>(0) ?? string.Empty);
            }
        }

        foreach (var child in current.GetNamespaceMembers())
            CollectSignalDeclarations(child, traces, metrics, logs, cancellationToken);
    }

    private static SortedSet<string> SignalSet(TelemetrySignal signal, SortedSet<string> traces, SortedSet<string> metrics, SortedSet<string> logs)
        => signal switch
        {
            TelemetrySignal.Traces => traces,
            TelemetrySignal.Metrics => metrics,
            TelemetrySignal.Logs => logs,
            _ => throw new InvalidOperationException("Unknown telemetry signal: " + signal),
        };

    private static bool IsAttribute(AttributeData attribute, string attributeName)
        => string.Equals(attribute.AttributeClass?.Name, attributeName, StringComparison.Ordinal);
}
