using System.Diagnostics;
using Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners;
using Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners.Semantics;
using Qyl.Telemetry.AutoInstrumentation.Internal;
using Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Qyl;
using DbAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes;
using ServerAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Server.ServerAttributes;

namespace Qyl.Telemetry.AutoInstrumentation.SqlClient;

/// <summary>
/// Subscribes to Microsoft.Data.SqlClient command events and extracts real command payload values
/// without IL rewriting or reflection.
/// </summary>
internal sealed class SqlClientDiagnosticListener : QylDiagnosticListenerSubscriber
{
    /// <inheritdoc/>
    protected override string ListenerName => "SqlClientDiagnosticListener";

    /// <inheritdoc/>
    protected override QylAutoInstrumentationSignal Signal => QylAutoInstrumentationSignal.Traces;

    /// <inheritdoc/>
    protected override string InstrumentationId => QylAutoInstrumentationIds.SqlClient;

    /// <summary>In-flight command start timestamps, keyed by the diagnostic OperationId. Entries are
    /// removed on the matching After/Error event; the size cap guards against events whose completion
    /// never fires (killed connections) from accumulating forever.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> PendingOperations = new();

    private const int PendingOperationsCap = 10_000;

    /// <inheritdoc/>
    protected override void OnEvent(string name, object? payload)
    {
        if (StringComparer.Ordinal.Equals(name, "Microsoft.Data.SqlClient.WriteCommandBefore"))
        {
            if (SqlClientPayloadReader.TryReadOperationStart(payload, out var operationId, out var startTimestamp) &&
                PendingOperations.Count < PendingOperationsCap)
            {
                PendingOperations[operationId] = startTimestamp;
            }

            return;
        }

        var isSuccess = StringComparer.Ordinal.Equals(name, "Microsoft.Data.SqlClient.WriteCommandAfter");
        var isError = StringComparer.Ordinal.Equals(name, "Microsoft.Data.SqlClient.WriteCommandError");

        if (!isSuccess && !isError)
            return;

        // Free the WriteCommandBefore entry on every completion event, before anything that can
        // return early. A payload shape this reader cannot parse fails deterministically, so a
        // leak here fills PendingOperationsCap and then every later span records zero duration.
        var pending = SqlClientPayloadReader.TryReadOperationId(payload, out var operationKey) &&
                      PendingOperations.TryRemove(operationKey, out var removedTimestamp)
            ? removedTimestamp
            : (long?)null;

        if (!SqlClientPayloadReader.TryRead(payload, isError, out var command))
            return;

        var duration = TimeSpan.Zero;
        if (pending is { } beforeTimestamp &&
            command.Timestamp is { } afterTimestamp &&
            afterTimestamp > beforeTimestamp)
        {
            duration = Stopwatch.GetElapsedTime(beforeTimestamp, afterTimestamp);
        }

        if (QylDbActivityPolicy.HasCurrentActivityFor(command.Command))
            return;

        var endTime = TimeProvider.System.GetUtcNow();
        using var activity = QylActivitySource.StartAt(
            SpanNames.DbSqlClient(command.QuerySummary, command.Operation, collectionName: null, storedProcedureName: null, command.Namespace, command.ServerAddress, command.ServerPort, DbAttributes.SystemNameValues.MicrosoftSqlServer),
            ActivityKind.Client,
            endTime - duration);
        activity?.SetEndTime(endTime.UtcDateTime);

        DatabaseSemantics.WriteCommand(
            activity,
            QylAttributes.InstrumentationDomainValues.DbSqlClient,
            DbAttributes.SystemNameValues.MicrosoftSqlServer,
            command.Namespace,
            command.Operation,
            command.QuerySummary,
            command.QueryText,
            QylAutoInstrumentationOptions.Current.SqlClientSetDbStatementForText,
            command.ErrorType);
        SemanticTagWriter.Set(activity, ServerAttributes.Address, command.ServerAddress);
        SemanticTagWriter.Set(activity, ServerAttributes.Port, command.ServerPort);
    }

}
