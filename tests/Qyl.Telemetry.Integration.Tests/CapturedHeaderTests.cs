using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Qyl.Telemetry.AutoInstrumentation.Internal;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class CapturedHeaderTests
{
    [TestMethod]
    public void GrpcMetadataCapture_MissingOrInactiveTarget_CapturesNothing()
    {
        using var request = GrpcRequest();
        using var response = GrpcResponse();
        using var activity = new Activity("metadata");

        QylCaptureHelpers.SetGrpcRequestMetadata(null, GrpcRequestMap, request);
        QylCaptureHelpers.SetGrpcResponseMetadata(activity, GrpcResponseMap, null);
        activity.IsAllDataRequested = false;
        QylCaptureHelpers.SetGrpcRequestMetadata(activity, GrpcRequestMap, request);
        QylCaptureHelpers.SetGrpcResponseMetadata(activity, GrpcResponseMap, response);

        Assert.IsEmpty(activity.TagObjects);
    }

    [TestMethod]
    [DataRow("rpc.request.metadata.x-text", new[] { "a", "a", "", "comma,kept" })]
    [DataRow("rpc.request.metadata.x-null-input", new[] { "", "" })]
    [DataRow("rpc.response.metadata.x-text", new[] { "header", "header", "trailer", "" })]
    [DataRow("rpc.request.metadata.trace-bin", new[] { "YWJj" })]
    [DataRow("rpc.response.metadata.trace-bin", new[] { "YWJj" })]
    public void GrpcMetadataCapture_ActiveActivity_PreservesEveryValueInOrder(string tag, string[] values)
    {
        using var request = GrpcRequest();
        using var response = GrpcResponse();
        using var activity = new Activity("metadata");

        QylCaptureHelpers.SetGrpcRequestMetadata(activity, GrpcRequestMap, request);
        QylCaptureHelpers.SetGrpcResponseMetadata(activity, GrpcResponseMap, response);

        Assert.AreSequenceEqual(values, Assert.IsInstanceOfType<string[]>(activity.GetTagItem(tag)));
    }

    [TestMethod]
    public void HttpClientHeaderCapture_BinarySuffixedHeader_KeepsItsText()
    {
        using var request = GrpcRequest();
        using var activity = new Activity("metadata");

        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, QylCapturedNameMap.Create("http.request.header.", ["trace-bin"]), request);

        Assert.AreSequenceEqual(["YWJj"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.trace-bin")));
    }

    [TestMethod]
    [DataRow("http.request.header.x-multi", new[] { "first", "first", "", "comma,kept" })]
    [DataRow("http.request.header.x-overlap", new[] { "primary", "content" })]
    [DataRow("http.request.header.content-type", new[] { "application/custom" })]
    [DataRow("http.response.header.x-multi", new[] { "response-a", "response-b" })]
    [DataRow("http.response.header.content-type", new[] { "application/response" })]
    public void HttpClientHeaderCapture_PrimaryAndContentHeaders_PreservesEveryValueInOrder(string tag, string[] values)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/");
        request.Content = new StringContent("body");
        request.Headers.TryAddWithoutValidation("X-Multi", ["first", "first", "", "comma,kept"]);
        request.Headers.TryAddWithoutValidation("X-Overlap", "primary");
        request.Content.Headers.TryAddWithoutValidation("X-Overlap", "content");
        request.Content.Headers.ContentType = new("application/custom");
        using var response = new HttpResponseMessage();
        response.Content = new StringContent("body");
        response.Headers.TryAddWithoutValidation("X-Multi", ["response-a", "response-b"]);
        response.Content.Headers.ContentType = new("application/response");
        using var activity = new Activity("headers");

        QylCaptureHelpers.SetHttpClientRequestHeaders(activity,
            QylCapturedNameMap.Create("http.request.header.", ["X-Multi", "X-Overlap", "Content-Type"]), request);
        QylCaptureHelpers.SetHttpClientResponseHeaders(activity,
            QylCapturedNameMap.Create("http.response.header.", ["X-Multi", "Content-Type"]), response);

        Assert.AreSequenceEqual(values, Assert.IsInstanceOfType<string[]>(activity.GetTagItem(tag)));
    }

    [TestMethod]
    public void HttpClientHeaderCapture_MissingOrInactiveTarget_CapturesNothing()
    {
        using var activity = new Activity("headers");
        using var request = OneHeaderRequest();

        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, OneHeaderMap, null);
        QylCaptureHelpers.SetHttpClientRequestHeaders(null, OneHeaderMap, request);
        activity.IsAllDataRequested = false;
        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, OneHeaderMap, request);

        Assert.IsEmpty(activity.TagObjects);
    }

    [TestMethod]
    public void HttpClientHeaderCapture_MissingResponse_KeepsTheRequestHeader()
    {
        using var activity = new Activity("headers");
        using var request = OneHeaderRequest();

        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, OneHeaderMap, request);
        QylCaptureHelpers.SetHttpClientResponseHeaders(activity, OneHeaderMap, null);

        Assert.AreSequenceEqual(["one"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.x-one")));
    }

    [TestMethod]
    public void HttpClientHeaderCapture_RequestWithoutContent_CapturesNoContentHeader()
    {
        using var activity = new Activity("headers");
        using var request = OneHeaderRequest();

        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, OneHeaderMap, request);

        Assert.IsNull(activity.GetTagItem("http.request.header.content-type"));
    }

    [TestMethod]
    public void CapturedNameMap_CaseVariantsAndBlankName_KeepsFourDistinctNames() =>
        Assert.AreEqual(4, PunctuatedMap.Count);

    [TestMethod]
    [DataRow("http.request.header.x-request-id", new[] { "dash" })]
    [DataRow("http.request.header.x_request_id", new[] { "under", "under", "" })]
    [DataRow("http.request.header.x.request.id", new[] { "dot,kept", "second" })]
    [DataRow("http.request.header.mcp-param-secret", new[] { "selected" })]
    public void RequestHeaderCapture_ConfiguredNames_PreservesEveryValueCaseInsensitively(string tag, string[] values)
    {
        using var activity = CapturePunctuatedHeaders();

        Assert.AreSequenceEqual(values, Assert.IsInstanceOfType<string[]>(activity.GetTagItem(tag)));
    }

    [TestMethod]
    public void RequestHeaderCapture_ConfiguredNames_CapturesOnlySelectedHeaders()
    {
        using var activity = CapturePunctuatedHeaders();

        Assert.HasCount(4, activity.TagObjects);
    }

    [TestMethod]
    public void RequestHeaderCapture_EmptyConfiguration_CapturesNothing()
    {
        using var activity = new Activity("capture");

        QylCaptureHelpers.SetRequestHeaders(activity, QylCapturedNameMap.Create("http.request.header.", []),
            new HeaderDictionary { ["x-visible"] = "present" });

        Assert.IsEmpty(activity.TagObjects);
    }

    private static QylCapturedNameMap GrpcRequestMap => QylCapturedNameMap.Create("rpc.request.metadata.", ["X-TEXT", "TRACE-BIN", "X-Null-Input"]);

    private static QylCapturedNameMap GrpcResponseMap => QylCapturedNameMap.Create("rpc.response.metadata.", ["x-text", "trace-bin"]);

    private static QylCapturedNameMap OneHeaderMap => QylCapturedNameMap.Create("http.request.header.", ["X-One", "Content-Type"]);

    private static QylCapturedNameMap PunctuatedMap => QylCapturedNameMap.Create("http.request.header.",
        ["X-Request-Id", "X_Request_Id", "X-REQUEST-ID", "X.Request.Id", "mcp-param-secret", " "]);

    private static HttpRequestMessage GrpcRequest()
    {
        var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation("trace-bin", "YWJj");
        request.Headers.TryAddWithoutValidation("X-Text", ["a", "a", "", "comma,kept"]);
        request.Headers.TryAddWithoutValidation("X-Null-Input", [null, ""]);
        return request;
    }

    private static HttpResponseMessage GrpcResponse()
    {
        var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("X-Text", ["header", "header"]);
        response.TrailingHeaders.TryAddWithoutValidation("x-text", ["trailer", ""]);
        response.TrailingHeaders.TryAddWithoutValidation("trace-bin", "YWJj");
        return response;
    }

    private static HttpRequestMessage OneHeaderRequest()
    {
        var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation("X-One", "one");
        return request;
    }

    private static Activity CapturePunctuatedHeaders()
    {
        var activity = new Activity("capture");
        QylCaptureHelpers.SetRequestHeaders(activity, PunctuatedMap, new HeaderDictionary
        {
            ["x-request-id"] = "dash",
            ["X_REQUEST_ID"] = new StringValues(["under", null, "under", ""]),
            ["x.request.id"] = new StringValues(["dot,kept", "second"]),
            ["unselected"] = "absent",
            ["mcp-param-secret"] = "selected",
        });
        return activity;
    }
}
