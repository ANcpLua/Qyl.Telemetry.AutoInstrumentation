using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.TestFixtures;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>HttpClient request and response header capture in a NativeAOT child process, per configuration and control.</summary>
[TestClass]
sealed class ClientHeaderCaptureTests : NativeCaptureTests<ClientHeaderCaptureTests>, INativeCaptureSuite
{
    private const string RequestHeaders = " X-Tenant-Id, X_Request_Id, X-Request-Id, X-REQUEST-ID, X.Request.Id, Content-Type, Content-Disposition, X-Custom-Multi ";
    private const string ResponseHeaders = "X-RateLimit-Remaining,Content-Type,X-Custom-Multi";
    private const string EmptyHeaders = " , \t, ";

    public static string Fixture => "ClientHeaderCapture";

    public static string Instrumentation => QylAutoInstrumentationIds.HttpClient;

    public static string RequestVariable => QylAutoInstrumentationOptions.HttpClientRequestHeadersVariable;

    public static string ResponseVariable => QylAutoInstrumentationOptions.HttpClientResponseHeadersVariable;

    public static IReadOnlyList<NativeCaptureCase> Cases =>
    [
        new(ClientCaptureProfiles.Configured, RequestHeaders, ResponseHeaders, ClientCaptureControl.Enabled, true, true),
        new(ClientCaptureProfiles.Request, RequestHeaders, null, ClientCaptureControl.Enabled, true, false),
        new(ClientCaptureProfiles.Response, null, ResponseHeaders, ClientCaptureControl.Enabled, false, true),
        new(ClientCaptureProfiles.None, null, null, ClientCaptureControl.Enabled, false, false),
        new(ClientCaptureProfiles.Empty, EmptyHeaders, EmptyHeaders, ClientCaptureControl.Enabled, false, false),
        new(ClientCaptureProfiles.Disabled, RequestHeaders, ResponseHeaders, ClientCaptureControl.InstrumentationDisabled, false, false),
        new(ClientCaptureProfiles.GlobalDisabled, RequestHeaders, ResponseHeaders, ClientCaptureControl.GlobalDisabled, false, false),
        new(ClientCaptureProfiles.Unsampled, RequestHeaders, ResponseHeaders, ClientCaptureControl.Unsampled, false, false),
        new(ClientCaptureProfiles.Suppressed, RequestHeaders, ResponseHeaders, ClientCaptureControl.Suppressed, false, false),
    ];
}
