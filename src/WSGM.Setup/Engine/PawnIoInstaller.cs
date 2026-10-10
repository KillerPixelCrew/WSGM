using System;
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
        return Install(Installation.InstalledVersion, Installation.IsInstalled,
            () => Installation.InstallAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    internal static DriverInstallResult Install(string? installedVersion, bool supported,
        Func<DriverInstallResult> install)
    {
        if (supported)
        {
            return new DriverInstallResult(true, false, false);
        }

        if (installedVersion is not null)
        {
            return new DriverInstallResult(false, false, false,
                $"PawnIO {installedVersion} is incompatible with this WSGM build. "
                + "The bundled installer cannot replace an existing copy. "
                + "Remove PawnIO in Windows Settings > Apps > Installed apps, then run WSGM setup's Repair. "
                + "The existing driver was left unchanged.");
        }

        return install();
    }

    internal static DriverInstallResult Uninstall()
    {
        return Installation.UninstallAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
