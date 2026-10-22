using System.Collections.ObjectModel;
using System.Globalization;
using Qyl.Telemetry.AutoInstrumentation.Internal;
using HttpAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Http.HttpAttributes;
using RpcAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Rpc.RpcAttributes;

namespace Qyl.Telemetry.AutoInstrumentation;

internal sealed class QylAutoInstrumentationOptions
{
    internal const string GlobalEnabledVariable = "OTEL_DOTNET_AUTO_INSTRUMENTATION_ENABLED";
    private const string TracesEnabledVariable = "OTEL_DOTNET_AUTO_TRACES_INSTRUMENTATION_ENABLED";
    private const string MetricsEnabledVariable = "OTEL_DOTNET_AUTO_METRICS_INSTRUMENTATION_ENABLED";
    private const string MetricsAdditionalSourcesVariable = "OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES";
    private const string LogsEnabledVariable = "OTEL_DOTNET_AUTO_LOGS_INSTRUMENTATION_ENABLED";
    private const string EntityFrameworkCoreDbStatementVariable =
        "OTEL_DOTNET_AUTO_ENTITYFRAMEWORKCORE_SET_DBSTATEMENT_FOR_TEXT";
    private const string SqlClientSetDbStatementVariable = "OTEL_DOTNET_AUTO_SQLCLIENT_SET_DBSTATEMENT_FOR_TEXT";
    private const string AspNetCoreRequestHeadersVariable =
        "OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS";
    private const string AspNetCoreResponseHeadersVariable =
        "OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_RESPONSE_HEADERS";
    internal const string HttpClientRequestHeadersVariable =
        "OTEL_DOTNET_AUTO_TRACES_HTTP_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS";
    internal const string HttpClientResponseHeadersVariable =
        "OTEL_DOTNET_AUTO_TRACES_HTTP_INSTRUMENTATION_CAPTURE_RESPONSE_HEADERS";
    internal const string GrpcRequestMetadataVariable =
        "OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_CAPTURE_REQUEST_METADATA";
    internal const string GrpcResponseMetadataVariable =
        "OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_CAPTURE_RESPONSE_METADATA";
    private const string AspNetCoreUrlQueryRedactionDisabledVariable =
        "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION";
    private const string HttpClientUrlQueryRedactionDisabledVariable =
        "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION";

    public static QylAutoInstrumentationOptions Current => CurrentHolder.Value;

    private readonly IReadOnlyDictionary<InstrumentationLookupKey, bool> _instrumentationEnabled;

    private QylAutoInstrumentationOptions(
        bool tracesEnabled,
        bool metricsEnabled,
        bool logsEnabled,
        IReadOnlyDictionary<InstrumentationLookupKey, bool> instrumentationEnabled,
        bool entityFrameworkCoreSetDbStatementForText,
        bool sqlClientSetDbStatementForText,
        string[] aspNetCoreCapturedRequestHeaders,
        string[] aspNetCoreCapturedResponseHeaders,
        string[] httpClientCapturedRequestHeaders,
        string[] httpClientCapturedResponseHeaders,
        string[] grpcCapturedRequestMetadata,
        string[] grpcCapturedResponseMetadata,
        string[] additionalMetricMeterNames,
        bool aspNetCoreUrlQueryRedactionDisabled,
        bool httpClientUrlQueryRedactionDisabled)
    {
        TracesEnabled = tracesEnabled;
        MetricsEnabled = metricsEnabled;
        LogsEnabled = logsEnabled;
        _instrumentationEnabled = instrumentationEnabled;
        EntityFrameworkCoreSetDbStatementForText = entityFrameworkCoreSetDbStatementForText;
        SqlClientSetDbStatementForText = sqlClientSetDbStatementForText;
        AspNetCoreCapturedRequestHeaderMap = QylCapturedNameMap.Create(HttpAttributes.RequestHeader + ".", aspNetCoreCapturedRequestHeaders);
        AspNetCoreCapturedResponseHeaderMap = QylCapturedNameMap.Create(HttpAttributes.ResponseHeader + ".", aspNetCoreCapturedResponseHeaders);
        HttpClientCapturedRequestHeaderMap = QylCapturedNameMap.Create(HttpAttributes.RequestHeader + ".", httpClientCapturedRequestHeaders);
        HttpClientCapturedResponseHeaderMap = QylCapturedNameMap.Create(HttpAttributes.ResponseHeader + ".", httpClientCapturedResponseHeaders);
        GrpcCapturedRequestMetadataMap = CreateGrpcMetadataMap(RpcAttributes.RequestMetadata + ".", grpcCapturedRequestMetadata);
        GrpcCapturedResponseMetadataMap = CreateGrpcMetadataMap(RpcAttributes.ResponseMetadata + ".", grpcCapturedResponseMetadata);
        AdditionalMetricMeterNames = additionalMetricMeterNames;
        AspNetCoreUrlQueryRedactionDisabled = aspNetCoreUrlQueryRedactionDisabled;
        HttpClientUrlQueryRedactionDisabled = httpClientUrlQueryRedactionDisabled;
    }

    private bool TracesEnabled { get; }

    public bool MetricsEnabled { get; }

    public bool LogsEnabled { get; }

    public bool EntityFrameworkCoreSetDbStatementForText { get; }

    public bool SqlClientSetDbStatementForText { get; }

    internal QylCapturedNameMap AspNetCoreCapturedRequestHeaderMap { get; }

    internal QylCapturedNameMap AspNetCoreCapturedResponseHeaderMap { get; }

    internal QylCapturedNameMap HttpClientCapturedRequestHeaderMap { get; }

    internal QylCapturedNameMap HttpClientCapturedResponseHeaderMap { get; }

    internal QylCapturedNameMap GrpcCapturedRequestMetadataMap { get; }

    internal QylCapturedNameMap GrpcCapturedResponseMetadataMap { get; }

    internal string[] AdditionalMetricMeterNames { get; }

    public bool AspNetCoreUrlQueryRedactionDisabled { get; }

    public bool HttpClientUrlQueryRedactionDisabled { get; }

    public bool IsInstrumentationEnabled(QylAutoInstrumentationSignal signal, string instrumentationId)
    {
        ArgumentNullException.ThrowIfNull(instrumentationId);

        return _instrumentationEnabled.TryGetValue(new InstrumentationLookupKey(signal, instrumentationId), out var enabled)
            ? enabled
            : IsSignalEnabled(signal);
    }

    public bool HasAnyActivityInstrumentationEnabled()
        => HasAnyInstrumentationEnabled(QylAutoInstrumentationSignal.Traces, QylDeclaredInstrumentations.Traces) ||
           HasAnyInstrumentationEnabled(QylAutoInstrumentationSignal.Logs, QylDeclaredInstrumentations.Logs);

    private static class CurrentHolder
    {
        internal static readonly QylAutoInstrumentationOptions Value = Load();
    }

    private static QylAutoInstrumentationOptions Load()
    {
        var globalEnabled = EnvironmentOptions.ReadBoolean(GlobalEnabledVariable) ?? true;
        var tracesEnabled = EnvironmentOptions.ReadBoolean(TracesEnabledVariable) ?? globalEnabled;
        var metricsEnabled = EnvironmentOptions.ReadBoolean(MetricsEnabledVariable) ?? globalEnabled;
        var logsEnabled = EnvironmentOptions.ReadBoolean(LogsEnabledVariable) ?? globalEnabled;
        var instrumentationEnabled = new Dictionary<InstrumentationLookupKey, bool>();

        AddSignalInstrumentations(instrumentationEnabled, QylAutoInstrumentationSignal.Traces, tracesEnabled, QylDeclaredInstrumentations.Traces);
        AddSignalInstrumentations(instrumentationEnabled, QylAutoInstrumentationSignal.Metrics, metricsEnabled, QylDeclaredInstrumentations.Metrics);
        AddSignalInstrumentations(instrumentationEnabled, QylAutoInstrumentationSignal.Logs, logsEnabled, QylDeclaredInstrumentations.Logs);

        return new QylAutoInstrumentationOptions(
            tracesEnabled,
            metricsEnabled,
            logsEnabled,
            new ReadOnlyDictionary<InstrumentationLookupKey, bool>(instrumentationEnabled),
            EnvironmentOptions.ReadBoolean(EntityFrameworkCoreDbStatementVariable) ?? false,
            EnvironmentOptions.ReadBoolean(SqlClientSetDbStatementVariable) ?? false,
            EnvironmentOptions.ReadList(AspNetCoreRequestHeadersVariable),
            EnvironmentOptions.ReadList(AspNetCoreResponseHeadersVariable),
            EnvironmentOptions.ReadList(HttpClientRequestHeadersVariable),
            EnvironmentOptions.ReadList(HttpClientResponseHeadersVariable),
            EnvironmentOptions.ReadList(GrpcRequestMetadataVariable),
            EnvironmentOptions.ReadList(GrpcResponseMetadataVariable),
            EnvironmentOptions.ReadList(MetricsAdditionalSourcesVariable, lowerCase: false),
            EnvironmentOptions.ReadBoolean(AspNetCoreUrlQueryRedactionDisabledVariable) ?? false,
            EnvironmentOptions.ReadBoolean(HttpClientUrlQueryRedactionDisabledVariable) ?? false);
    }

    private static QylCapturedNameMap CreateGrpcMetadataMap(string prefix, string[] names) =>
        QylCapturedNameMap.Create(prefix, names);

    private static void AddSignalInstrumentations(
        Dictionary<InstrumentationLookupKey, bool> target,
        QylAutoInstrumentationSignal signal,
        bool signalDefault,
        string[] instrumentationIds)
    {
        ArgumentNullException.ThrowIfNull(instrumentationIds);

        foreach (var instrumentationId in instrumentationIds)
        {
            if (string.IsNullOrWhiteSpace(instrumentationId))
                continue;

            var variable = BuildSignalSpecificVariable(signal, instrumentationId);
            target[new InstrumentationLookupKey(signal, instrumentationId)] =
                EnvironmentOptions.ReadBoolean(variable) ?? signalDefault;
        }
    }

    private bool IsSignalEnabled(QylAutoInstrumentationSignal signal)
        => signal switch
        {
            QylAutoInstrumentationSignal.Traces => TracesEnabled,
            QylAutoInstrumentationSignal.Metrics => MetricsEnabled,
            QylAutoInstrumentationSignal.Logs => LogsEnabled,
            _ => false,
        };

    private bool HasAnyInstrumentationEnabled(QylAutoInstrumentationSignal signal, string[] instrumentationIds)
    {
        foreach (var instrumentationId in instrumentationIds)
        {
            if (IsInstrumentationEnabled(signal, instrumentationId))
                return true;
        }

        return false;
    }

    private static string BuildSignalSpecificVariable(QylAutoInstrumentationSignal signal, string instrumentationId)
        => signal switch
        {
            QylAutoInstrumentationSignal.Traces => "OTEL_DOTNET_AUTO_TRACES_" + instrumentationId + "_INSTRUMENTATION_ENABLED",
            QylAutoInstrumentationSignal.Metrics => "OTEL_DOTNET_AUTO_METRICS_" + instrumentationId + "_INSTRUMENTATION_ENABLED",
            QylAutoInstrumentationSignal.Logs => "OTEL_DOTNET_AUTO_LOGS_" + instrumentationId + "_INSTRUMENTATION_ENABLED",
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
        };

    private static class EnvironmentOptions
    {
        internal static bool? ReadBoolean(string variable)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "0", StringComparison.Ordinal) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return bool.TryParse(value, out var parsed) ? parsed : null;
        }

        internal static string[] ReadList(string variable, bool lowerCase = true)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
                return [];

            var items = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => item.Length > 0);
            if (lowerCase)
                items = items.Select(static item => item.ToLower(CultureInfo.InvariantCulture));

            return [.. items.Distinct(StringComparer.Ordinal)];
        }
    }

    private readonly record struct InstrumentationLookupKey(QylAutoInstrumentationSignal Signal, string InstrumentationId);
}
