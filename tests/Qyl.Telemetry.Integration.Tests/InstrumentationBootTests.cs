using Qyl.Telemetry.AutoInstrumentation;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class InstrumentationBootTests
{
    [TestMethod]
    public void BootOnce_CalledTwiceWithOneFlag_SubscribesOnce()
    {
        var booted = 0;
        var subscriptions = 0;

        QylInstrumentation.BootOnce(ref booted, () => subscriptions++);
        QylInstrumentation.BootOnce(ref booted, () => subscriptions++);

        Assert.AreEqual(1, subscriptions);
    }
}
