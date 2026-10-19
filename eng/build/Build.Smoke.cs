namespace Qyl.Build;

sealed partial class QylBuild
{
    Target Smoke => definition => definition
        .Description("Package smoke test: package and ProjectReference consumers, managed and NativeAOT, verified stdout, warning-free AOT publish, SqlClient package assets")
        .After(AotPublishGate)
        .Executes(SmokeGate.Run);
}
