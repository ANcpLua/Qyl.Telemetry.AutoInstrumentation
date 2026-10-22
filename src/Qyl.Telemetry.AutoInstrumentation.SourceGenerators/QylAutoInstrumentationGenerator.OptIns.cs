using ANcpLua.Roslyn.Utilities;
using Microsoft.CodeAnalysis;

namespace Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

/// <summary>
/// The <c>QYL1002</c> lane: a library whose native <c>ActivitySource</c> qyl subscribes, but whose
/// telemetry the consumer has to turn on in their own code.
/// </summary>
/// <remarks>
/// One table, one diagnostic, one message shape. A row is not a claim that the library is silent
/// without the call — that differs per library, and the row's consequence text says which case it
/// is. The GenAI rows report at Warning: without the call the chat, agent and workflow spans never
/// exist, which is telemetry silently off rather than telemetry reduced. MySql.Data has no row on
/// purpose: its add-on package's <c>AddConnectorNet()</c> is <c>AddSource("connector-net")</c> and
/// nothing more, which is exactly what <c>AddQyl()</c> already does, so there is nothing for a
/// consumer to add.
/// </remarks>
public sealed partial class QylAutoInstrumentationGenerator
{
    /// <summary>One library's opt-in: what names it, what turns its telemetry on, and what is lost without it.</summary>
    /// <param name="Library">The library the message names.</param>
    /// <param name="TriggerNamespace">The namespace (or a parent of it) of a call that uses the library.</param>
    /// <param name="TriggerType">When set, the type (or a base of the type) that declares the using call.</param>
    /// <param name="TriggerMethods">The names of the calls that use the library.</param>
    /// <param name="OptInMethod">The name of the call that turns its telemetry on.</param>
    /// <param name="OptInNamespace">When set, the namespace (or a parent of it) that call must come from.</param>
    /// <param name="OptInType">When set, the library's telemetry wrapper, whose construction is the same opt-in.</param>
    /// <param name="OptInDisplay">The call as the message shows it.</param>
    /// <param name="Consequence">What is lost without the call.</param>
    /// <param name="Severity">Info for reduced telemetry, Warning for telemetry that stays off.</param>
    private readonly record struct OptInDeclaration(
        string Library,
        string TriggerNamespace,
        string TriggerType,
        EquatableArray<string> TriggerMethods,
        string OptInMethod,
        string OptInNamespace,
        string OptInType,
        string OptInDisplay,
        string Consequence,
        DiagnosticSeverity Severity);

    private static readonly ImmutableArray<OptInDeclaration> OptIns =
    [
        new(
            "GraphQL.NET",
            "GraphQL",
            string.Empty,
            ImmutableArray.Create("AddGraphQL").AsEquatableArray(),
            "UseTelemetry",
            string.Empty,
            string.Empty,
            "IGraphQLBuilder.UseTelemetry()",
            "GraphQL.NET's ActivitySource stays silent, so the application produces no GraphQL spans at all",
            DiagnosticSeverity.Info),
        new(
            "Oracle ODP.NET",
            "Oracle.ManagedDataAccess",
            string.Empty,
            ImmutableArray.Create("ExecuteScalar", "ExecuteNonQuery", "ExecuteReader").AsEquatableArray(),
            "AddOracleDataProviderInstrumentation",
            string.Empty,
            string.Empty,
            "TracerProviderBuilder.AddOracleDataProviderInstrumentation()",
            "ODP.NET still emits its command spans, but only with db.system, db.odp.roundtrip.count, "
            + "db.odp.roundtrip.duration and db.response.returned_rows; db.name, db.user, db.statement, "
            + "server.address, server.port, db.odp.connection.id, db.odp.sql_id and exception recording "
            + "stay off",
            DiagnosticSeverity.Info),
        new(
            "Microsoft.Extensions.AI",
            "Microsoft.Extensions.AI",
            string.Empty,
            ImmutableArray.Create("GetResponseAsync", "GetStreamingResponseAsync").AsEquatableArray(),
            "UseOpenTelemetry",
            "Microsoft.Extensions.AI",
            "Microsoft.Extensions.AI.OpenTelemetryChatClient",
            "ChatClientBuilder.UseOpenTelemetry()",
            "the Experimental.Microsoft.Extensions.AI ActivitySource stays silent, so no chat or execute_tool span "
            + "and no token usage is recorded",
            DiagnosticSeverity.Warning),
        new(
            "Microsoft.Agents.AI",
            "Microsoft.Agents.AI",
            "Microsoft.Agents.AI.AIAgent",
            ImmutableArray.Create("RunAsync", "RunStreamingAsync").AsEquatableArray(),
            "UseOpenTelemetry",
            "Microsoft.Agents.AI",
            "Microsoft.Agents.AI.OpenTelemetryAgent",
            "AIAgentBuilder.UseOpenTelemetry()",
            "the Experimental.Microsoft.Agents.AI ActivitySource stays silent, so no invoke_agent span is recorded",
            DiagnosticSeverity.Warning),
        new(
            "Microsoft.Agents.AI.Workflows",
            "Microsoft.Agents.AI.Workflows",
            "Microsoft.Agents.AI.Workflows.WorkflowBuilder",
            ImmutableArray.Create("Build").AsEquatableArray(),
            "WithOpenTelemetry",
            "Microsoft.Agents.AI.Workflows",
            string.Empty,
            "WorkflowBuilder.WithOpenTelemetry()",
            "the Microsoft.Agents.AI.Workflows ActivitySource stays silent, so no workflow span is recorded",
            DiagnosticSeverity.Warning),
    ];

    /// <summary>What one invocation or construction said about the opt-in table: it used a library, or it opted one in.</summary>
    private readonly record struct OptInSignal(int Row, bool IsOptIn, Location? Location);

    private static OptInSignal ScanOptIn(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var node = context.Node;
        var compilation = context.SemanticModel.Compilation;
        if (compilation.AssemblyName is { } assemblyName && QylRuntimeAssemblies.Contains(assemblyName))
            return default;

        if (context.SemanticModel.GetSymbolInfo(node, cancellationToken).Symbol is not IMethodSymbol symbol)
            return default;

        for (var row = 0; row < OptIns.Length; row++)
        {
            var declaration = OptIns[row];
            if (IsOptIn(symbol, in declaration))
                return new OptInSignal(row + 1, true, null);

            if (!ContainsOrdinal(declaration.TriggerMethods, symbol.Name) || !IsTriggerType(symbol, declaration.TriggerType))
                continue;

            if (IsInNamespace(symbol, declaration.TriggerNamespace) ||
                (symbol.ReducedFrom is { Parameters.Length: > 0 } reduced &&
                 IsInNamespace(reduced.Parameters[0].Type, declaration.TriggerNamespace)))
            {
                return new OptInSignal(row + 1, false, node.GetLocation());
            }
        }

        return default;
    }

    private static bool IsOptIn(IMethodSymbol symbol, in OptInDeclaration declaration)
        => symbol.MethodKind is MethodKind.Constructor
            ? declaration.OptInType.Length > 0 &&
              string.Equals(symbol.ContainingType.ToDisplayString(), declaration.OptInType, StringComparison.Ordinal)
            : string.Equals(symbol.Name, declaration.OptInMethod, StringComparison.Ordinal) &&
              (declaration.OptInNamespace.Length is 0 || IsInNamespace(symbol, declaration.OptInNamespace));

    private static bool IsTriggerType(IMethodSymbol symbol, string triggerType)
    {
        if (triggerType.Length is 0)
            return true;

        for (var type = symbol.ContainingType; type is not null; type = type.BaseType)
        {
            if (string.Equals(type.ToDisplayString(), triggerType, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsOrdinal(EquatableArray<string> values, string value)
    {
        foreach (var candidate in values)
        {
            if (string.Equals(candidate, value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsInNamespace(IMethodSymbol symbol, string namespaceName)
        => IsInNamespace(symbol.ContainingType, namespaceName)
           || (symbol.ContainingNamespace is { } declaring &&
               NamespaceMatches(declaring.ToDisplayString(), namespaceName));

    private static bool IsInNamespace(ITypeSymbol? type, string namespaceName)
        => type?.ContainingNamespace is { } containing && NamespaceMatches(containing.ToDisplayString(), namespaceName);

    private static bool NamespaceMatches(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.Ordinal)
           || (actual.Length > expected.Length && actual.StartsWithOrdinal(expected) && actual[expected.Length] is '.');

    private static void ReportMissingOptIns(SourceProductionContext context, ImmutableArray<OptInSignal> signals)
    {
        if (signals.IsDefaultOrEmpty)
            return;

        var optedIn = new HashSet<int>();
        var used = new Dictionary<int, Location>();
        foreach (var signal in signals)
        {
            if (signal.Row is 0)
                continue;

            if (signal.IsOptIn)
                optedIn.Add(signal.Row);
            else if (signal.Location is { } location && !used.ContainsKey(signal.Row))
                used[signal.Row] = location;
        }

        // Stable order so the build log is reproducible whatever order Roslyn visited the trees in.
        var rows = new List<int>(used.Keys);
        rows.Sort();
        foreach (var row in rows)
        {
            if (optedIn.Contains(row))
                continue;

            var declaration = OptIns[row - 1];
            context.ReportDiagnostic(Diagnostic.Create(
                QylGeneratorDiagnostics.NativeTelemetryOptInMissing,
                used[row],
                declaration.Severity,
                additionalLocations: null,
                properties: null,
                declaration.Library,
                declaration.OptInDisplay,
                declaration.Consequence));
        }
    }
}
