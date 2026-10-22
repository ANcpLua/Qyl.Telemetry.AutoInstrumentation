using System.Data;
using Qyl.Telemetry.AutoInstrumentation.Internal;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class DbQuerySummaryCommentTests
{
    private const string NestedCommentBeforeTarget = "SELECT * FROM /* legacy /* old_table */ archive_orders */ orders";
    private const string NestedCommentCarryingValue = "SELECT * FROM /* /* x */ alice_at_example_com */ users";
    private const string SelectOrders = "SELECT orders";
    private const string SelectUsers = "SELECT users";

    [TestMethod]
    [DataRow(NestedCommentBeforeTarget, SelectOrders,
        ArgumentsDisplayName = "nested comment before the target")]
    [DataRow(NestedCommentCarryingValue, SelectUsers,
        ArgumentsDisplayName = "nested comment carrying a value")]
    public void QuerySummary_NestedBlockComment_SkipsCommentText(string commandText, string expected)
    {
        var (operation, summary) = QylDbQuerySummary.Describe(CommandType.Text, commandText);
        Assert.AreEqual("SELECT", operation);
        Assert.AreEqual(expected, summary);
    }
}
