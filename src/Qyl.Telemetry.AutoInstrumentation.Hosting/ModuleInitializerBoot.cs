using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Qyl.Telemetry.AutoInstrumentation.Hosting;

/// <summary>
/// AOT-native package bootstrap for applications that reference the hosting package.
///
/// <para>
/// When an app references <c>Qyl.Telemetry.AutoInstrumentation.Hosting</c>, the compiler emits a call to
/// <see cref="Boot"/> as part of the assembly's module-init sequence. The boot path is ordinary
/// compiled C# that NativeAOT understands natively.
/// </para>
///
/// <para>
/// The initializer applies the process-wide runtime switches qyl depends on. The specialist EF Core
/// and SqlClient packages carry the only remaining <c>DiagnosticListener</c> subscribers and
/// register their own. All sites it touches are AOT-safe and avoid dynamic discovery.
/// </para>
/// </summary>
internal static class ModuleInitializerBoot
{
    private static int _booted;

    /// <summary>The single qyl entry point invoked by the CLR at module load.</summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "Module-init IS the AOT-native attach mechanism this package ships; opt-in is the act of referencing Qyl.Telemetry.AutoInstrumentation.Hosting.")]
    public static void Boot()
        => QylInstrumentation.BootOnce(ref _booted);
}
