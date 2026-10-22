using System.Net.Sockets;
using OpenTelemetry.Exporter;

namespace Qyl;

/// <summary>
/// Zero-config local collector discovery. Probes the conventional qyl endpoints once per process
/// and caches the result; the standard OTLP environment variables always take precedence and are
/// handled by the exporter itself, so discovery only runs when nothing is configured.
/// </summary>
/// <remarks>
/// The probe costs four 100 ms connect attempts plus a DNS lookup for <c>qyl</c> when nothing is
/// listening, so it never runs on the caller's thread. <see cref="Start"/> hands back a task the
/// exporter's options callback awaits when the OpenTelemetry SDK builds its pipeline — after the
/// host has finished wiring itself — and <c>AddQyl()</c> itself touches no socket.
/// </remarks>
internal static class CollectorDiscovery
{
    private static readonly Lazy<Task<CollectorDestination?>> ProbeTask =
        new(static () => Task.Run(ProbeForCollector), LazyThreadSafetyMode.ExecutionAndPublication);

    // OTLP/HTTP first: it works over HTTP/1.1 everywhere, while plaintext gRPC needs an explicit
    // HTTP/2-without-TLS arrangement. The "qyl" hostname covers the container-network default.
    private static readonly (string Host, int Port, OtlpExportProtocol Protocol)[] ProbeTargets =
    [
        ("localhost", 4318, OtlpExportProtocol.HttpProtobuf),
        ("localhost", 4317, OtlpExportProtocol.Grpc),
        ("qyl", 4318, OtlpExportProtocol.HttpProtobuf),
        ("qyl", 4317, OtlpExportProtocol.Grpc)
    ];

    /// <summary>Starts the one process-wide probe, or returns the running one. Never blocks.</summary>
    internal static Task<CollectorDestination?> Start() => ProbeTask.Value;

    /// <summary>
    /// The probe's answer, waited for at most <paramref name="timeout"/>. Called from the exporter's
    /// options callback, which the SDK invokes when it builds the pipeline; a probe that has not
    /// finished by then yields no endpoint rather than holding the build open.
    /// </summary>
    internal static CollectorDestination? WaitForEndpoint(Task<CollectorDestination?> probe, TimeSpan timeout)
        => probe.Wait(timeout) ? probe.GetAwaiter().GetResult() : null;

    private static CollectorDestination? ProbeForCollector()
    {
        // QYL_ENDPOINT names a collector without claiming OTEL_EXPORTER_OTLP_ENDPOINT, which every
        // OTLP exporter in the process would honor. Discovery only runs when the standard variable
        // is unset, so this stays the documented fallback rather than an override of it. An
        // unparseable value means "configured, but wrong" — probing on would silently export
        // somewhere the operator did not ask for.
        var configured = Environment.GetEnvironmentVariable("QYL_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri)
                ? new CollectorDestination(configuredUri, OtlpExportProtocol.HttpProtobuf)
                : null;
        }

        foreach (var (host, port, protocol) in ProbeTargets)
        {
            if (TcpProbe(host, port))
                return new CollectorDestination(new Uri($"http://{host}:{port}"), protocol);
        }

        return null;
    }

    internal static bool TcpProbe(string host, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            socket.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            // Connection refused / host unreachable / DNS failure / no answer within 100 ms — nothing
            // is listening there. The cancellation aborts the pending connect.
            return false;
        }
    }
}

internal sealed record CollectorDestination(Uri Endpoint, OtlpExportProtocol Protocol);
