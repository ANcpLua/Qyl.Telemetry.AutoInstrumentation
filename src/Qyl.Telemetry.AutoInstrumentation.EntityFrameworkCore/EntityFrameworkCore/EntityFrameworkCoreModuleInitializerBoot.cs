using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore;

internal static class EntityFrameworkCoreModuleInitializerBoot
{
    private static int _booted;

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "Module-init is the AOT-native attach mechanism for the EFCore-specific qyl package.")]
    public static void Boot()
        => QylInstrumentation.BootOnce(ref _booted, static () => new EntityFrameworkCoreDiagnosticListener().Subscribe());
}
