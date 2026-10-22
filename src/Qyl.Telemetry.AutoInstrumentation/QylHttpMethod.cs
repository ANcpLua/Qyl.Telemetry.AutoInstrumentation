using HttpAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Http.HttpAttributes;

namespace Qyl.Telemetry.AutoInstrumentation;

internal static class QylHttpMethod
{
    private static bool IsKnown(string method)
        => method is not HttpAttributes.RequestMethodValues.Other && HttpAttributes.RequestMethodValues.Contains(method);

    public static string Normalize(string? method)
    {
        if (string.IsNullOrEmpty(method))
            return HttpAttributes.RequestMethodValues.Other;

        var normalized = method.ToUpperInvariant();
        return IsKnown(normalized) ? normalized : HttpAttributes.RequestMethodValues.Other;
    }

    /// <summary>
    /// Normalizes <paramref name="method"/> and reports the raw value as <paramref name="original"/> when it
    /// is non-standard or differs from its normalized spelling. Per OTel, http.request.method_original MUST be
    /// set when http.request.method is <c>_OTHER</c> or a canonical spelling of the original ("get" reported as
    /// GET); <paramref name="original"/> is null for a method sent exactly as a standard one.
    /// </summary>
    public static string Normalize(string? method, out string? original)
    {
        var normalized = Normalize(method);
        original = !string.IsNullOrEmpty(method) &&
                   (normalized is HttpAttributes.RequestMethodValues.Other || !string.Equals(normalized, method, StringComparison.Ordinal))
            ? method
            : null;
        return normalized;
    }
}
