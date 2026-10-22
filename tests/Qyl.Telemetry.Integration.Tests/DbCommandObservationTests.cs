using System.Diagnostics.Metrics;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class DbCommandObservationTests
{
    [TestMethod]
    public async Task DbCommandObserveAsync_FaultedTask_RecordsOperationDuration()
    {
        var measurements = new List<double>();
        using var meterListener = ListenToDatabaseDurations(measurements);

        var observed = QylInterceptedDbCommand.ObserveAsync(
            Task.FromException<object?>(new InvalidOperationException("connection lost")), null, 1, QylAutoInstrumentationIds.SqlClient);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => observed);
        Assert.HasCount(1, measurements);
    }

    [TestMethod]
    public async Task DbCommandObserveAsync_CompletedTask_RecordsOperationDuration()
    {
        var measurements = new List<double>();
        using var meterListener = ListenToDatabaseDurations(measurements);

        await QylInterceptedDbCommand.ObserveAsync(Task.FromResult<object?>(1), null, 1, QylAutoInstrumentationIds.SqlClient);

        Assert.HasCount(1, measurements);
    }

    private static MeterListener ListenToDatabaseDurations(List<double> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == QylMetricMeters.DatabaseMeterName)
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => measurements.Add(value));
        listener.Start();
        return listener;
    }
}
