using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Qyl.Telemetry.AutoInstrumentation.Internal;

internal static class QylCaptureHelpers
{
    internal static void SetHttpClientRequestHeaders(Activity? activity, QylCapturedNameMap map, HttpRequestMessage? request)
    {
        if (CanCapture(activity, map, request))
            SetHttpHeaders(activity, map, request.Headers, request.Content?.Headers);
    }

    internal static void SetHttpClientResponseHeaders(Activity? activity, QylCapturedNameMap map, HttpResponseMessage? response)
    {
        if (CanCapture(activity, map, response))
            SetHttpHeaders(activity, map, response.Headers, response.Content.Headers);
    }

    internal static void SetGrpcRequestMetadata(Activity? activity, QylCapturedNameMap map, HttpRequestMessage? request)
    {
        // gRPC binary metadata is already base64-encoded by the HTTP transport.
        if (CanCapture(activity, map, request))
            SetHttpHeaders(activity, map, request.Headers, null);
    }

    internal static void SetGrpcResponseMetadata(Activity? activity, QylCapturedNameMap map, HttpResponseMessage? response)
    {
        if (CanCapture(activity, map, response))
            SetHttpHeaders(activity, map, response.Headers, response.TrailingHeaders);
    }

    private static bool CanCapture([NotNullWhen(true)] Activity? activity, QylCapturedNameMap map, [NotNullWhen(true)] object? message)
        => activity is { IsAllDataRequested: true } && message is not null && map.Count is not 0;

    private static void SetHttpHeaders(Activity activity, QylCapturedNameMap map, HttpHeaders primary, HttpHeaders? additional)
    {
        for (var index = 0; index < map.Count; index++)
        {
            var name = map.GetLookupName(index);
            var values = new List<string>();
            AddHeaderValues(primary, name, values);
            if (additional is not null) AddHeaderValues(additional, name, values);
            if (values.Count > 0) activity.SetTag(map.GetTagName(index), values.ToArray());
        }
    }

    private static void AddHeaderValues(HttpHeaders headers, string name, List<string> values)
    {
        if (!headers.NonValidated.TryGetValues(name, out var exposed)) return;
        // This API exposes non-null strings: HttpHeaders normalizes null inputs to empty
        // strings before capture. Preserve those exposed empty values as well as duplicates.
        // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Http/src/System/Net/Http/Headers/HttpHeaders.cs#L148-L153
        values.AddRange(exposed);
    }

    public static void SetRequestHeaders(
        Activity? activity,
        QylCapturedNameMap configuredHeaders,
        IHeaderDictionary headers)
    {
        if (activity is null || configuredHeaders.Count is 0)
            return;

        for (var index = 0; index < configuredHeaders.Count; index++)
        {
            var lookupName = configuredHeaders.GetLookupName(index);
            if (headers.TryGetValue(lookupName, out var values) && values.Count > 0)
                activity.SetTag(configuredHeaders.GetTagName(index), ToTagValues(values));
        }
    }

    public static string RedactQueryValues(string query)
    {
        if (query.IndexOf('=', StringComparison.Ordinal) < 0)
            return query;

        var builder = new StringBuilder(2 * query.Length);
        for (var index = 0; index < query.Length; index++)
        {
            var current = query[index];
            if (current is '=')
            {
                builder.Append("=Redacted");
                index++;
                while (index < query.Length && query[index] is not '&')
                    index++;
                if (index < query.Length)
                    builder.Append('&');
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private static string[] ToTagValues(StringValues values)
        => values.Count is 0
            ? []
            : [.. values.Where(static value => value is not null).Select(static value => value!)];

}
