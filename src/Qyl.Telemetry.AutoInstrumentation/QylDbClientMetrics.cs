using System.Diagnostics.Metrics;
using DbAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Db.DbAttributes;
using DbMetricDefinitions = Qyl.Telemetry.SemanticConventions.Metrics.DbMetricDefinitions;

namespace Qyl.Telemetry.AutoInstrumentation;

internal static class QylDbClientMetrics
{
    // The instrument is the registry's row, name and unit together. Nothing about
    // db.client.operation.duration is typed here, so the definition is the only place it is
    // spelled and a change to it reaches this instrument by rebuild.
    private static readonly Meter Meter = new(QylMetricMeters.DatabaseMeterName);
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        DbMetricDefinitions.DbClientOperationDuration.Name,
        DbMetricDefinitions.DbClientOperationDuration.Unit);

    public static long GetTimestamp()
        => OperationDuration.Enabled ? TimeProvider.System.GetTimestamp() : 0;

    public static void RecordDuration(long startTimestamp, string instrumentationId)
    {
        ArgumentNullException.ThrowIfNull(instrumentationId);

        if (startTimestamp is 0 || !IsRecordingEnabled(instrumentationId))
            return;

        var elapsed = TimeProvider.System.GetElapsedTime(startTimestamp);
        if (elapsed.TotalSeconds >= 0)
        {
            OperationDuration.Record(
                elapsed.TotalSeconds,
                new KeyValuePair<string, object?>(DbAttributes.SystemName, Internal.QylDbActivityPolicy.GetDbSystemName(instrumentationId)));
        }
    }

    internal static bool IsRecordingEnabled(string instrumentationId)
        => OperationDuration.Enabled && ShouldRecord(instrumentationId);

    private static bool ShouldRecord(string instrumentationId)
        => instrumentationId is QylAutoInstrumentationIds.SqlClient or QylAutoInstrumentationIds.Npgsql
           && QylAutoInstrumentationOptions.Current.IsInstrumentationEnabled(QylAutoInstrumentationSignal.Metrics, instrumentationId);
}
