using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.TestFixtures;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>Grpc.Net.Client request and response metadata capture in a NativeAOT child process, per configuration and control.</summary>
[TestClass]
sealed class ClientMetadataCaptureTests : NativeCaptureTests<ClientMetadataCaptureTests>, INativeCaptureSuite
{
    private const string RequestHeaders = " X-Demo-Md, X-Request-Id, X_Request_Id, X-REQUEST-ID, X.Request.Id, X-Multi, TRACE-BIN ";
    private const string ResponseHeaders = "X-Demo-Res-Md,X-Demo-Trailer-Md,X-Overlap,trace-bin";
    private const string EmptyHeaders = " , \t, ";
    private const string HttpHeader = "X-Demo-Md";
    private const string BinaryHeader = "TRACE-BIN";

    public static string Fixture => "ClientMetadataCapture";

    public static string Instrumentation => QylAutoInstrumentationIds.GrpcNetClient;

    public static string RequestVariable => QylAutoInstrumentationOptions.GrpcRequestMetadataVariable;

    public static string ResponseVariable => QylAutoInstrumentationOptions.GrpcResponseMetadataVariable;

    public static IReadOnlyList<NativeCaptureCase> Cases =>
    [
        new(ClientCaptureProfiles.Configured, RequestHeaders, ResponseHeaders, ClientCaptureControl.Enabled, true, true),
        new(ClientCaptureProfiles.Request, RequestHeaders, null, ClientCaptureControl.Enabled, true, false),
        new(ClientCaptureProfiles.Response, null, ResponseHeaders, ClientCaptureControl.Enabled, false, true),
        new(ClientCaptureProfiles.None, null, null, ClientCaptureControl.Enabled, false, false),
        new(ClientCaptureProfiles.Empty, EmptyHeaders, EmptyHeaders, ClientCaptureControl.Enabled, false, false),
        new(ClientCaptureProfiles.BinaryOnly, BinaryHeader, BinaryHeader, ClientCaptureControl.Enabled, true, true),
        new(ClientCaptureProfiles.HttpOnly, null, null, ClientCaptureControl.Enabled, false, false, HttpHeader),
        new(ClientCaptureProfiles.Disabled, RequestHeaders, ResponseHeaders, ClientCaptureControl.InstrumentationDisabled, false, false),
        new(ClientCaptureProfiles.GlobalDisabled, RequestHeaders, ResponseHeaders, ClientCaptureControl.GlobalDisabled, false, false),
        new(ClientCaptureProfiles.Unsampled, RequestHeaders, ResponseHeaders, ClientCaptureControl.Unsampled, false, false),
        new(ClientCaptureProfiles.Suppressed, RequestHeaders, ResponseHeaders, ClientCaptureControl.Suppressed, false, false),
    ];
}
