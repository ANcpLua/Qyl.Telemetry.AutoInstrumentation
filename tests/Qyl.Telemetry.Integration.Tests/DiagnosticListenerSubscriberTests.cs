using System.Diagnostics;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class DiagnosticListenerSubscriberTests
{
    [TestMethod]
    public void SubscriberDispose_TwoListenersWithOneName_StopsEverySubscription()
    {
        var name = "Qyl.Subscriber." + Guid.NewGuid().ToString("N");
        using var first = new DiagnosticListener(name);
        using var second = new DiagnosticListener(name);
        var subscriber = new RecordingSubscriber(name);
        subscriber.Subscribe();
        first.Write("before", null);
        second.Write("before", null);
        Assert.AreSequenceEqual(["before", "before"], [.. subscriber.Events]);

        subscriber.Dispose();
        first.Write("after", null);
        second.Write("after", null);

        Assert.AreSequenceEqual(["before", "before"], [.. subscriber.Events],
            "Events still arrive after Dispose.");
    }

    private sealed class RecordingSubscriber(string listenerName) : QylDiagnosticListenerSubscriber
    {
        public List<string> Events { get; } = [];

        protected override string ListenerName => listenerName;

        protected override QylAutoInstrumentationSignal Signal => QylAutoInstrumentationSignal.Traces;

        protected override string InstrumentationId => "QYLTESTSUBSCRIBER";

        protected override void OnEvent(string name, object? payload) => Events.Add(name);
    }
}
