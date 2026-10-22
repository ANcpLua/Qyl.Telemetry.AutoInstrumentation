using System.Diagnostics;
using Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners;
using Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners.Semantics;
using Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Qyl;

namespace Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore;

/// <summary>
/// Subscribes to <c>Microsoft.EntityFrameworkCore</c> command events and extracts real EFCore
/// command payload values without IL rewriting.
/// </summary>
internal sealed class EntityFrameworkCoreDiagnosticListener : QylDiagnosticListenerSubscriber
{
    /// <inheritdoc/>
    protected override string ListenerName => "Microsoft.EntityFrameworkCore";

    /// <inheritdoc/>
    protected override QylAutoInstrumentationSignal Signal => QylAutoInstrumentationSignal.Traces;

    /// <inheritdoc/>
    protected override string InstrumentationId => QylAutoInstrumentationIds.EntityFrameworkCore;

    /// <inheritdoc/>
    protected override void OnEvent(string name, object? payload)
    {
        if (!StringComparer.Ordinal.Equals(name, "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted") &&
            !StringComparer.Ordinal.Equals(name, "Microsoft.EntityFrameworkCore.Database.Command.CommandError"))
        {
            return;
        }

        if (!EntityFrameworkCorePayloadReader.TryRead(payload, out var command))
            return;

        using var activity = QylActivitySource.StartAt(
            SpanNames.DbSqlClient(command.QuerySummary, command.Operation, collectionName: null, storedProcedureName: null, command.Namespace, serverAddress: null, serverPort: null, command.DbSystem),
            ActivityKind.Client,
            command.StartTime);
        activity?.SetEndTime((command.StartTime + command.Duration).UtcDateTime);

        DatabaseSemantics.WriteCommand(
            activity,
            QylAttributes.InstrumentationDomainValues.DbEfCore,
            command.DbSystem,
            command.Namespace,
            command.Operation,
            command.QuerySummary,
            command.QueryText,
            QylAutoInstrumentationOptions.Current.EntityFrameworkCoreSetDbStatementForText,
            command.ErrorType);
    }
}
