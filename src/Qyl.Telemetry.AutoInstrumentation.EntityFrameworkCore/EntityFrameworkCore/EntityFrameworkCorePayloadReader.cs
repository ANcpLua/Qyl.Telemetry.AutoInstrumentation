using Microsoft.EntityFrameworkCore.Diagnostics;
using Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners.Semantics;
using Qyl.Telemetry.AutoInstrumentation.Internal;
using DbAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes;
using DbIncubatingAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Db.DbAttributes;

namespace Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore;

internal static class EntityFrameworkCorePayloadReader
{
    public static bool TryRead(object? payload, out EntityFrameworkCoreCommand command)
    {
        if (payload is not CommandEventData commandEvent)
        {
            command = default;
            return false;
        }

        var (operation, summary) = QylDbQuerySummary.Describe(commandEvent.Command.CommandType, commandEvent.Command.CommandText);
        command = new EntityFrameworkCoreCommand(
            DbSystem: MapProviderName(commandEvent.Context?.Database.ProviderName),
            Namespace: DatabaseSemantics.NullIfWhiteSpace(commandEvent.Command.Connection?.Database),
            Operation: operation,
            QuerySummary: summary,
            QueryText: commandEvent.Command.CommandText,
            ErrorType: payload is CommandErrorEventData errorEvent
                ? errorEvent.Exception.GetType().FullName
                : null,
            StartTime: commandEvent is CommandEndEventData endEvent ? endEvent.StartTime : TimeProvider.System.GetUtcNow(),
            Duration: commandEvent is CommandEndEventData timedEvent ? timedEvent.Duration : TimeSpan.Zero);

        return true;
    }

    // Every provider that raises relational command events speaks SQL; an unmapped one is other_sql.
    private static string MapProviderName(string? providerName)
        => providerName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => DbIncubatingAttributes.SystemNameValues.Sqlite,
            "Microsoft.EntityFrameworkCore.SqlServer" => DbAttributes.SystemNameValues.MicrosoftSqlServer,
            "Npgsql.EntityFrameworkCore.PostgreSQL" => DbAttributes.SystemNameValues.Postgresql,
            "Pomelo.EntityFrameworkCore.MySql" => DbAttributes.SystemNameValues.Mysql,
            "MySql.EntityFrameworkCore" => DbAttributes.SystemNameValues.Mysql,
            "Oracle.EntityFrameworkCore" => DbIncubatingAttributes.SystemNameValues.OracleDb,
            "IBM.EntityFrameworkCore" => DbIncubatingAttributes.SystemNameValues.IbmDb2,
            _ => DbIncubatingAttributes.SystemNameValues.OtherSql,
        };
}

internal readonly record struct EntityFrameworkCoreCommand(
    string DbSystem,
    string? Namespace,
    string? Operation,
    string? QuerySummary,
    string? QueryText,
    string? ErrorType,
    DateTimeOffset StartTime,
    TimeSpan Duration);
