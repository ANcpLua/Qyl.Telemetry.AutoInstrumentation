using System.Diagnostics;
using System.Globalization;
using Qyl.Telemetry.AutoInstrumentation.Internal;
using Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Qyl;
using DbAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes;
using DbIncubatingAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Db.DbAttributes;

namespace Qyl.Telemetry.AutoInstrumentation.GeneratedCode;

/// <summary>StackExchange.Redis command spans.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
[QylIntegration(QylAutoInstrumentationIds.StackExchangeRedis)]
[QylIntercept("StackExchange.Redis.IDatabaseAsync", Shape = QylShapes.RedisCommand, Start = nameof(Command))]
public static class QylInterceptedRedis
{
    /// <summary>Starts the client span for the resolved wire command on the receiving database.</summary>
    public static Activity? Command([QylFromShape] string? operationName, [QylFromReceiver("Database")] int databaseIndex)
    {
        var databaseNamespace = databaseIndex >= 0 ? databaseIndex.ToString(CultureInfo.InvariantCulture) : null;
        if (QylActivityFactory.StartTraceActivity(
                QylAutoInstrumentationIds.StackExchangeRedis,
                SpanNames.DbClient(querySummary: null, operationName, collectionName: null, storedProcedureName: null, databaseNamespace, serverAddress: null, serverPort: null, DbIncubatingAttributes.SystemNameValues.Redis),
                ActivityKind.Client,
                QylAttributes.InstrumentationDomainValues.DbRedis) is not { } activity)
        {
            return null;
        }

        QylActivityTags.SetDb(activity, DbIncubatingAttributes.SystemNameValues.Redis, operationName, operationName);
        if (databaseNamespace is not null)
            activity.SetTag(DbAttributes.Namespace, databaseNamespace);

        return activity;
    }

    /// <summary>
    /// Normalizes the command text a caller passes to <c>IDatabaseAsync.ExecuteAsync</c> into the
    /// command name StackExchange.Redis puts on the wire, which upper-cases the caller's token.
    /// Returns <see langword="null"/> for text that is not a single command token, so an
    /// unbounded value is left off the span instead of becoming a span dimension.
    /// </summary>
    [JetBrains.Annotations.UsedImplicitly]
    public static string? NormalizeCommandText(string? command)
    {
        if (command is null)
            return null;

        var trimmed = command.Trim();
        if (trimmed.Length is 0)
            return null;

        foreach (var character in trimmed)
        {
            if (char.IsWhiteSpace(character))
                return null;
        }

        return trimmed.ToUpperInvariant();
    }
}
