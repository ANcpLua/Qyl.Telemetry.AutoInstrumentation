using System.Data;
using Qyl.Telemetry.AutoInstrumentation.Internal;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class DbQuerySummaryTargetTests
{
    private const string UpdateTop = "UPDATE TOP (10) dbo.Orders SET Status = 1";
    private const string UpdateTopPercent = "UPDATE TOP (10) PERCENT dbo.Orders SET Status = 1";
    private const string UpdateOrders = "UPDATE dbo.Orders";

    [TestMethod]
    [DataRow(UpdateTop, UpdateOrders, ArgumentsDisplayName = "UPDATE TOP (n)")]
    [DataRow(UpdateTopPercent, UpdateOrders, ArgumentsDisplayName = "UPDATE TOP (n) PERCENT")]
    public void QuerySummary_UpdateTop_NamesUpdatedTable(string commandText, string expected)
    {
        var (operation, summary) = QylDbQuerySummary.Describe(CommandType.Text, commandText);
        Assert.AreEqual("UPDATE", operation);
        Assert.AreEqual(expected, summary);
    }
}
