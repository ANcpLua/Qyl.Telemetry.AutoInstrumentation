namespace Qyl.Build;

sealed partial class QylBuild
{
    Target OtlpEvidence => definition => definition
        .Description("OTLP wire evidence: a package consumer's export decoded as ExportTraceServiceRequest by a loopback receiver, url.full redacted on the wire")
        .After(AotPublishGate)
        .Executes(() => OtlpWireEvidence.Run(_updateVerified));
}
