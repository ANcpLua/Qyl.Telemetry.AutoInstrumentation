using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore;
using Qyl.Telemetry.AutoInstrumentation.SqlClient;
using Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Qyl;
using DbAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes;
using DbIncubatingAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Db.DbAttributes;
using ErrorAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Error.ErrorAttributes;
using ServerAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Server.ServerAttributes;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class DatabaseDiagnosticListenerTests
{
    private const string SqlClientWriteCommandBefore = "Microsoft.Data.SqlClient.WriteCommandBefore";
    private const string SqlClientWriteCommandAfter = "Microsoft.Data.SqlClient.WriteCommandAfter";
    private const string SqlClientWriteCommandError = "Microsoft.Data.SqlClient.WriteCommandError";
    private const string EntityFrameworkCoreCommandExecuted = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";
    private const string EntityFrameworkCoreCommandError = "Microsoft.EntityFrameworkCore.Database.Command.CommandError";
    private const string SelectOrders = "SELECT * FROM orders";

    private static readonly DateTimeOffset CommandStart = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SqlClientWriteCommandAfter_SelectOnTcpServer_WritesDatabaseTags()
    {
        var spans = new List<Activity>();
        using var listener = ListenToQylSpans(spans);
        using var connection = new SqlConnection("Server=tcp:orders.example,1433;Database=shop");
        using var command = new SqlCommand(SelectOrders, connection);
        IObserver<KeyValuePair<string, object?>> observer = new SqlClientDiagnosticListener();

        observer.OnNext(new(SqlClientWriteCommandAfter, new Dictionary<string, object>
        {
            ["OperationId"] = Guid.NewGuid(),
            ["Command"] = command,
        }));

        Assert.AreSequenceEqual(
            [
                new(QylAttributes.InstrumentationDomain, QylAttributes.InstrumentationDomainValues.DbSqlClient),
                new(DbAttributes.SystemName, DbAttributes.SystemNameValues.MicrosoftSqlServer),
                new(DbAttributes.Namespace, "shop"),
                new(DbAttributes.OperationName, "SELECT"),
                new(DbAttributes.QuerySummary, "SELECT orders"),
                new(ServerAttributes.Address, "orders.example"),
                new(ServerAttributes.Port, 1433),
            ],
            Assert.ContainsSingle(spans).TagObjects);
    }

    [TestMethod]
    public void SqlClientWriteCommandError_InvalidOperation_RecordsErrorStatus()
    {
        var spans = new List<Activity>();
        using var listener = ListenToQylSpans(spans);
        using var connection = new SqlConnection("Server=tcp:orders.example,1433;Database=shop");
        using var command = new SqlCommand(SelectOrders, connection);
        IObserver<KeyValuePair<string, object?>> observer = new SqlClientDiagnosticListener();

        observer.OnNext(new(SqlClientWriteCommandError, new Dictionary<string, object>
        {
            ["OperationId"] = Guid.NewGuid(),
            ["Command"] = command,
            ["Exception"] = new InvalidOperationException("connection lost"),
        }));

        var span = Assert.ContainsSingle(spans);
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, span.GetTagItem(ErrorAttributes.Type));
    }

    [TestMethod]
    public void SqlClientWriteCommandAfter_OneSecondOfTimestampsAfterBefore_LastsOneSecond()
    {
        var spans = new List<Activity>();
        using var listener = ListenToQylSpans(spans);
        using var connection = new SqlConnection("Server=tcp:orders.example,1433;Database=shop");
        using var command = new SqlCommand(SelectOrders, connection);
        IObserver<KeyValuePair<string, object?>> observer = new SqlClientDiagnosticListener();
        var operationId = Guid.NewGuid();
        const long beforeTimestamp = 1_000;

        observer.OnNext(new(SqlClientWriteCommandBefore, new Dictionary<string, object>
        {
            ["OperationId"] = operationId,
            ["Command"] = command,
            ["Timestamp"] = beforeTimestamp,
        }));
        observer.OnNext(new(SqlClientWriteCommandAfter, new Dictionary<string, object>
        {
            ["OperationId"] = operationId,
            ["Command"] = command,
            ["Timestamp"] = beforeTimestamp + TimeProvider.System.TimestampFrequency,
        }));

        Assert.AreEqual(TimeSpan.FromSeconds(1), Assert.ContainsSingle(spans).Duration);
    }

    [TestMethod]
    public void EntityFrameworkCoreCommandExecuted_SelectCommand_WritesDatabaseTags()
    {
        var spans = new List<Activity>();
        using var listener = ListenToQylSpans(spans);
        using var connection = new SqlConnection("Server=tcp:orders.example,1433;Database=shop");
        using var command = new SqlCommand(SelectOrders, connection);
        IObserver<KeyValuePair<string, object?>> observer = new EntityFrameworkCoreDiagnosticListener();

        observer.OnNext(new(EntityFrameworkCoreCommandExecuted, Executed(connection, command)));

        Assert.AreSequenceEqual(
            [
                new(QylAttributes.InstrumentationDomain, QylAttributes.InstrumentationDomainValues.DbEfCore),
                new(DbAttributes.SystemName, DbIncubatingAttributes.SystemNameValues.OtherSql),
                new(DbAttributes.Namespace, "shop"),
                new(DbAttributes.OperationName, "SELECT"),
                new(DbAttributes.QuerySummary, "SELECT orders"),
            ],
            Assert.ContainsSingle(spans).TagObjects);
    }

    [TestMethod]
    public void EntityFrameworkCoreCommandError_InvalidOperation_RecordsErrorStatus()
    {
        var spans = new List<Activity>();
        using var listener = ListenToQylSpans(spans);
        using var connection = new SqlConnection("Server=tcp:orders.example,1433;Database=shop");
        using var command = new SqlCommand(SelectOrders, connection);
        IObserver<KeyValuePair<string, object?>> observer = new EntityFrameworkCoreDiagnosticListener();

        observer.OnNext(new(EntityFrameworkCoreCommandError, new CommandErrorEventData(
            null!, null!, connection, command, SelectOrders, null, DbCommandMethod.ExecuteReader, Guid.NewGuid(), Guid.NewGuid(),
            new InvalidOperationException("connection lost"), false, false, CommandStart, TimeSpan.FromMilliseconds(5), CommandSource.LinqQuery)));

        var span = Assert.ContainsSingle(spans);
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, span.GetTagItem(ErrorAttributes.Type));
    }

    [TestMethod]
    public void EntityFrameworkCoreCommandExecuted_PaddedDatabaseName_ReportsTrimmedNamespace()
    {
        var spans = new List<Activity>();
        using var listener = ListenToQylSpans(spans);
        using var connection = new SqlConnection("Server=tcp:orders.example,1433;Database=' shop '");
        using var command = new SqlCommand(SelectOrders, connection);
        IObserver<KeyValuePair<string, object?>> observer = new EntityFrameworkCoreDiagnosticListener();

        observer.OnNext(new(EntityFrameworkCoreCommandExecuted, Executed(connection, command)));

        Assert.AreEqual("shop", Assert.ContainsSingle(spans).GetTagItem(DbAttributes.Namespace));
    }

    private static CommandExecutedEventData Executed(DbConnection connection, DbCommand command)
        => new(null!, null!, connection, command, command.CommandText, null, DbCommandMethod.ExecuteReader, Guid.NewGuid(), Guid.NewGuid(),
            null, false, false, CommandStart, TimeSpan.FromMilliseconds(5), CommandSource.LinqQuery);

    private static ActivityListener ListenToQylSpans(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == QylActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
