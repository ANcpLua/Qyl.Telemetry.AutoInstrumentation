using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Qyl.Telemetry.AutoInstrumentation.Internal;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class CapturedHeaderTests
{
    [TestMethod]
    public void Grpc_message_capture_preserves_text_and_binary_headers_then_trailers()
    {
        using var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation("trace-bin", "YWJj");
        request.Headers.TryAddWithoutValidation("X-Text", ["a", "a", "", "comma,kept"]);
        request.Headers.TryAddWithoutValidation("X-Null-Input", [null, ""]);
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("X-Text", ["header", "header"]);
        response.TrailingHeaders.TryAddWithoutValidation("x-text", ["trailer", ""]);
        response.TrailingHeaders.TryAddWithoutValidation("trace-bin", "YWJj");
        using var activity = new Activity("metadata");
        var requestMap = QylCapturedNameMap.Create("rpc.request.metadata.", ["X-TEXT", "TRACE-BIN", "X-Null-Input"]);
        var responseMap = QylCapturedNameMap.Create("rpc.response.metadata.", ["x-text", "trace-bin"]);
        QylCaptureHelpers.SetGrpcRequestMetadata(null, requestMap, request);
        QylCaptureHelpers.SetGrpcResponseMetadata(activity, responseMap, null);
        activity.IsAllDataRequested = false;
        QylCaptureHelpers.SetGrpcRequestMetadata(activity, requestMap, request);
        QylCaptureHelpers.SetGrpcResponseMetadata(activity, responseMap, response);
        Assert.IsEmpty(activity.TagObjects);
        activity.IsAllDataRequested = true;
        QylCaptureHelpers.SetGrpcRequestMetadata(activity, requestMap, request);
        QylCaptureHelpers.SetGrpcResponseMetadata(activity, responseMap, response);
        Assert.AreSequenceEqual(["a", "a", "", "comma,kept"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("rpc.request.metadata.x-text")));
        Assert.AreSequenceEqual(["", ""], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("rpc.request.metadata.x-null-input")));
        Assert.AreSequenceEqual(["header", "header", "trailer", ""], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("rpc.response.metadata.x-text")));
        Assert.AreSequenceEqual(["YWJj"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("rpc.request.metadata.trace-bin")));
        Assert.AreSequenceEqual(["YWJj"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("rpc.response.metadata.trace-bin")));
        QylCaptureHelpers.SetHttpClientRequestHeaders(activity,
            QylCapturedNameMap.Create("http.request.header.", ["trace-bin"]), request);
        Assert.AreSequenceEqual(["YWJj"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.trace-bin")));
    }

    [TestMethod]
    public void Http_messages_capture_primary_and_content_headers_without_splitting_or_deduplication()
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
        var requestMap = QylCapturedNameMap.Create("http.request.header.", ["X-Multi", "X-Overlap", "Content-Type"]);
        var responseMap = QylCapturedNameMap.Create("http.response.header.", ["X-Multi", "Content-Type"]);
        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, requestMap, request);
        QylCaptureHelpers.SetHttpClientResponseHeaders(activity, responseMap, response);
        Assert.AreSequenceEqual(["first", "first", "", "comma,kept"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.x-multi")));
        Assert.AreSequenceEqual(["primary", "content"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.x-overlap")));
        Assert.AreSequenceEqual(["application/custom"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.content-type")));
        Assert.AreSequenceEqual(["response-a", "response-b"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.response.header.x-multi")));
        Assert.AreSequenceEqual(["application/response"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.response.header.content-type")));
    }

    [TestMethod]
    public void Http_capture_handles_missing_messages_content_and_inactive_activities()
    {
        var map = QylCapturedNameMap.Create("http.request.header.", ["X-One", "Content-Type"]);
        using var activity = new Activity("headers");
        using var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation("X-One", "one");
        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, map, null);
        QylCaptureHelpers.SetHttpClientRequestHeaders(null, map, request);
        activity.IsAllDataRequested = false;
        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, map, request);
        Assert.IsEmpty(activity.TagObjects);
        activity.IsAllDataRequested = true;
        QylCaptureHelpers.SetHttpClientRequestHeaders(activity, map, request);
        QylCaptureHelpers.SetHttpClientResponseHeaders(activity, map, null);
        Assert.AreSequenceEqual(["one"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.x-one")));
        Assert.IsNull(activity.GetTagItem("http.request.header.content-type"));
    }

    [TestMethod]
    public void Configured_headers_preserve_punctuation_arrays_and_case_insensitive_selection()
    {
        var map = QylCapturedNameMap.Create("http.request.header.",
            ["X-Request-Id", "X_Request_Id", "X-REQUEST-ID", "X.Request.Id", "mcp-param-secret", " "]);
        Assert.AreEqual(4, map.Count);
        var headers = new HeaderDictionary
        {
            ["x-request-id"] = "dash",
            ["X_REQUEST_ID"] = new StringValues(["under", null, "under", ""]),
            ["x.request.id"] = new StringValues(["dot,kept", "second"]),
            ["unselected"] = "absent",
            ["mcp-param-secret"] = "selected"
        };
        using var activity = new Activity("capture");
        QylCaptureHelpers.SetRequestHeaders(activity, map, headers);
        Assert.AreSequenceEqual(["dash"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.x-request-id")));
        Assert.AreSequenceEqual(["under", "under", ""], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.x_request_id")));
        Assert.AreSequenceEqual(["dot,kept", "second"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.x.request.id")));
        Assert.AreSequenceEqual(["selected"], Assert.IsInstanceOfType<string[]>(activity.GetTagItem("http.request.header.mcp-param-secret")));
        Assert.HasCount(4, activity.TagObjects);
    }

    [TestMethod]
    public void Empty_configuration_captures_nothing()
    {
        using var activity = new Activity("capture");
        QylCaptureHelpers.SetRequestHeaders(activity, QylCapturedNameMap.Create("http.request.header.", []),
            new HeaderDictionary { ["x-visible"] = "present" });
        Assert.IsEmpty(activity.TagObjects);
    }
}
