using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WSGM.DeviceLab.Transports;
using WSGM.Security;
using static WSGM.Interop.Kernel32;

namespace WSGM.DeviceLab.Wizard;

/// <summary>The pinned PawnIO installer, read from the lock file compiled into Device Lab.</summary>
internal sealed record PawnIoPin
{
    /// <summary>Pinned version.</summary>
    public required string Version { get; init; }

    /// <summary>Installer SHA-256, upper-case hex.</summary>
    public required string AssetSha256 { get; init; }

    /// <summary>SHA-1 thumbprint of the certificate that signs the installer and uninstaller.</summary>
    public required string SignerThumbprint { get; init; }

    /// <summary>Arguments for a silent install.</summary>
    public required string[] InstallArguments { get; init; }

    /// <summary>Arguments for a silent uninstall.</summary>
    public required string[] UninstallArguments { get; init; }

    /// <summary>HKLM uninstall key.</summary>
    public required string UninstallKey { get; init; }

    /// <summary>Oldest installed version the lab accepts without replacing it.</summary>
    public required string MinimumInstalledVersion { get; init; }
}

/// <summary>What is installed now.</summary>
/// <param name="InstalledVersion">DisplayVersion from the uninstall key, or null when not installed.</param>
/// <param name="InstallLocation">InstallLocation from the uninstall key.</param>
/// <param name="DeviceOpened">Whether the control device opened, which proves the driver is running.</param>
/// <param name="DeviceError">The open error, when it failed.</param>
internal sealed record PawnIoStatus(
    string? InstalledVersion,
    string? InstallLocation,
    bool DeviceOpened,
    int DeviceError);

/// <summary>What preflight should do about PawnIO.</summary>
internal enum PawnIoAction
{
    /// <summary>Installed, recent enough and running.</summary>
    None,

    /// <summary>Not installed; install the pinned version.</summary>
    Install,

    /// <summary>Installed but older than the minimum; ask the tester before replacing it.</summary>
    AskToReplace,

    /// <summary>Installed but the driver does not answer, for example blocked by HVCI; report it.</summary>
    ReportNotRunning,

    /// <summary>This build carries no installer; report PawnIO as unavailable.</summary>
    Unavailable
}

/// <summary>
///     Detects, installs and removes PawnIO for the wizard's SMU and EC stages.
/// </summary>
/// <remarks>
///     The installer is embedded only when <c>eng/acquire-pawnio.ps1</c> ran before the build. The lock
///     file is always embedded. The wizard runs elevated, so the installer is extracted into a new
///     directory under the Windows temp folder that only administrators and SYSTEM can open, and the
///     file stays open without write or delete sharing from the moment it is hashed until the installer
///     exits: the bytes checked against the pinned SHA-256 and signer are the bytes that run. The lab
///     never passes <c>-unrestricted</c>, which would switch off PawnIO's module signature check.
/// </remarks>
internal static class PawnIoSetup
{
    private const string InstallerResource = "WSGM.DeviceLab.PawnIO.setup.exe";

    private static readonly PawnIoInstallation Installation =
        new(typeof(PawnIoSetup).Assembly, "WSGM.DeviceLab.PawnIO.lock.json", InstallerResource);

    private static readonly Lazy<PawnIoPin> LazyPin = new(LabPins.PawnIoInstaller);

    /// <summary>The pin compiled into this build.</summary>
    public static PawnIoPin Pin => LazyPin.Value;

    /// <summary>Whether this build carries the installer.</summary>
    public static bool InstallerBundled =>
        typeof(PawnIoSetup).Assembly.GetManifestResourceInfo(InstallerResource) is not null;

    /// <summary>Decides what preflight should do.</summary>
    /// <param name="status">What is installed now.</param>
    /// <param name="pin">The pinned installer.</param>
    /// <param name="installerBundled">Whether this build carries the installer.</param>
    /// <returns>The action.</returns>
    public static PawnIoAction Decide(PawnIoStatus status, PawnIoPin pin, bool installerBundled)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(pin);
        if (status.InstalledVersion is null)
        {
            return installerBundled ? PawnIoAction.Install : PawnIoAction.Unavailable;
        }

        if (!Version.TryParse(status.InstalledVersion, out var installed)
            || installed < Version.Parse(pin.MinimumInstalledVersion))
        {
            return installerBundled ? PawnIoAction.AskToReplace : PawnIoAction.Unavailable;
        }

        return status.DeviceOpened ? PawnIoAction.None : PawnIoAction.ReportNotRunning;
    }

    /// <summary>Reads what is installed now.</summary>
    /// <returns>The status.</returns>
    public static PawnIoStatus Detect()
    {
        string? version = null;
        string? location = null;
        using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                   .OpenSubKey(Pin.UninstallKey))
        {
            if (key is not null)
            {
                version = key.GetValue("DisplayVersion") as string;
                location = key.GetValue("InstallLocation") as string;
            }
        }

        using var handle = CreateFileW(@"\\.\PawnIO", GenericRead, 7, 0, OpenExisting, 0, 0);
        var error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return new PawnIoStatus(version, location, !handle.IsInvalid, error);
    }

    /// <summary>Installs the pinned version using the shared signed installer mechanism.</summary>
    public static async Task<string?> InstallAsync(CancellationToken cancellationToken)
    {
        var result = await Installation.InstallAsync(cancellationToken).ConfigureAwait(false);
        return result.Error ?? (result.RestartRequired ? "PawnIO installation requires a restart." : null);
    }

    /// <summary>Removes a lab-owned installation using its signed uninstaller.</summary>
    public static async Task<string?> UninstallAsync(PawnIoStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        var result = await Installation.UninstallAsync(cancellationToken).ConfigureAwait(false);
        return result.Error ?? (result.RestartRequired ? "PawnIO removal requires a restart." : null);
    }

    internal static string CreateAdministratorsOnlyDirectory(string purpose = "pawnio")
    {
        return PawnIoInstallation.CreateAdministratorsOnlyDirectory(purpose);
    }
}
