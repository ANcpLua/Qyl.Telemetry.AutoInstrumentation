using System.Diagnostics;
using Qyl.Telemetry.AutoInstrumentation.Internal;
using SessionAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Session.SessionAttributes;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class SessionBaggageTests
{
    [TestMethod]
    public void Stamp_UnreservedBaggageMember_SetsSessionId()
    {
        using var activity = new Activity("server");
        activity.AddBaggage(SessionAttributes.Id, "Agent-1.a_b~c");

        QylSessionBaggage.Stamp(activity);

        Assert.AreEqual("Agent-1.a_b~c", activity.GetTagItem(SessionAttributes.Id));
    }

    [TestMethod]
    public void Stamp_DotsOnlyBaggageMember_LeavesSessionIdUnset()
    {
        using var activity = new Activity("server");
        activity.AddBaggage(SessionAttributes.Id, "..");

        QylSessionBaggage.Stamp(activity);

        Assert.IsNull(activity.GetTagItem(SessionAttributes.Id));
    }

    [TestMethod]
    public void Stamp_ReservedCharacterBaggageMember_LeavesSessionIdUnset()
    {
        using var activity = new Activity("server");
        activity.AddBaggage(SessionAttributes.Id, "agent/1");

        QylSessionBaggage.Stamp(activity);

        Assert.IsNull(activity.GetTagItem(SessionAttributes.Id));
    }
}
