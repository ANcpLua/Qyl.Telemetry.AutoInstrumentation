using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Qyl.Telemetry.AutoInstrumentation.SqlClient;

internal static class SqlClientModuleInitializerBoot
{
    private static int _booted;

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "Module-init is the AOT-native attach mechanism for the SqlClient-specific qyl package.")]
    public static void Boot()
        => QylInstrumentation.BootOnce(ref _booted, static () => new SqlClientDiagnosticListener().Subscribe());
}
