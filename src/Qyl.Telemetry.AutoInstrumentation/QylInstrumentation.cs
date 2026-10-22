namespace Qyl.Telemetry.AutoInstrumentation;

/// <summary>
/// Process-wide activation shared by the public host and package module initializer.
/// </summary>
internal static class QylInstrumentation
{
    /// <summary>
    /// Instrumentation-scope version stamped on every qyl-emitted span/metric (via
    /// <see cref="QylActivitySource"/>). Baked at build time from the root
    /// <c>Directory.Build.props</c> <c>&lt;Version&gt;</c> — which CI packs unchanged and uses for the
    /// post-verification <c>v*</c> tag — via the generated <c>QylVersionInfo</c> const (see the
    /// <c>GenerateQylVersionInfo</c> target in the core project). No reflection; it always matches
    /// the shipped package and is never hand-maintained.
    /// </summary>
    public static readonly string Version = QylVersionInfo.Version;

    private static int _activated;

    /// <summary>
    /// The module-initializer boot sequence each package shares: the first call through
    /// <paramref name="booted"/> activates qyl and runs the package's <paramref name="subscribe"/>;
    /// later calls do nothing.
    /// </summary>
    internal static void BootOnce(ref int booted, Action? subscribe = null)
    {
        if (Interlocked.Exchange(ref booted, 1) == 1)
            return;

        Activate();
        subscribe?.Invoke();
    }

    /// <summary>
    /// Applies the process-wide runtime switches qyl instrumentation depends on.
    /// </summary>
    public static void Activate()
    {
        if (Interlocked.Exchange(ref _activated, 1) == 1)
            return;

        // The outbound HTTP span is the BCL's own, and the BCL redacts the query of its url.full to
        // "*" by itself. qyl does not rewrite what a library emitted, so the only thing left to bind
        // is the consumer's opt-out: OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION
        // now flips the runtime's own switch instead of a qyl-owned redaction that no longer exists.
        if (QylAutoInstrumentationOptions.Current.HttpClientUrlQueryRedactionDisabled)
            AppContext.SetSwitch("System.Net.Http.DisableUriRedaction", true);

        if (QylAutoInstrumentationOptions.Current.IsInstrumentationEnabled(
                QylAutoInstrumentationSignal.Traces,
                QylAutoInstrumentationIds.Azure))
        {
            AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
        }

    }
}
