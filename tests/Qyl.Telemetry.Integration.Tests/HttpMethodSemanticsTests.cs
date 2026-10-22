using Qyl.Telemetry.AutoInstrumentation;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class HttpMethodSemanticsTests
{
    private const string LowerCaseGet = "get";
    private const string MixedCasePost = "Post";
    private const string Get = "GET";
    private const string PostMethod = "POST";

    [TestMethod]
    [DataRow(LowerCaseGet, Get)]
    [DataRow(MixedCasePost, PostMethod)]
    public void HttpMethod_Canonicalized_ReportsOriginalSpelling(string method, string canonical)
    {
        var normalized = QylHttpMethod.Normalize(method, out var original);
        Assert.AreEqual(canonical, normalized);
        Assert.AreEqual(method, original);
    }

    [TestMethod]
    public void HttpMethod_ExactKnownMethod_ReportsNoOriginal()
    {
        Assert.AreEqual(Get, QylHttpMethod.Normalize(Get, out var original));
        Assert.IsNull(original);
    }
}
