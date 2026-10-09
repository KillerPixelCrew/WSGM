using System.Threading;
using WSGM.Security;

namespace WSGM.Setup.Engine;

/// <summary>Setup's optional PawnIO component, backed by the same signed installer as Device Lab.</summary>
internal static class PawnIoInstaller
{
    private static readonly PawnIoInstallation Installation = new(typeof(PawnIoInstaller).Assembly,
        "WSGM.PawnIO.Pin", "WSGM.PawnIO.Installer");

    internal static bool IsInstalled()
    {
        return Installation.IsInstalled;
    }

    internal static bool IsPresent()
    {
        return Installation.InstalledVersion is not null;
    }

    internal static DriverInstallResult Install()
    {
        return Installation.InstallAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static DriverInstallResult Uninstall()
    {
        return Installation.UninstallAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
