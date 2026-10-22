using System.Diagnostics;
using Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Qyl;
using DbAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes;

namespace Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners.Semantics;

internal static class DatabaseSemantics
{
    public static bool ShouldWriteQueryText(string? queryText, bool captureText)
        => captureText && !string.IsNullOrWhiteSpace(queryText);

    internal static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static void WriteCommand(
        Activity? activity,
        string domain,
        string systemName,
        string? dbNamespace,
        string? operation,
        string? querySummary,
        string? queryText,
        bool captureQueryText,
        string? errorType)
    {
        SemanticTagWriter.Set(activity, QylAttributes.InstrumentationDomain, domain);
        SemanticTagWriter.Set(activity, DbAttributes.SystemName, systemName);
        SemanticTagWriter.Set(activity, DbAttributes.Namespace, dbNamespace);
        SemanticTagWriter.Set(activity, DbAttributes.OperationName, operation);
        SemanticTagWriter.Set(activity, DbAttributes.QuerySummary, querySummary);
        if (DatabaseSemantics.ShouldWriteQueryText(queryText, captureQueryText))
        {
            SemanticTagWriter.Set(activity, DbAttributes.QueryText, queryText);
        }

        ErrorStatusSemantics.SetError(activity, errorType);
    }
}
