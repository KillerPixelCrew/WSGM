using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
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
    private const string LockResource = "WSGM.DeviceLab.PawnIO.lock.json";
    private const string InstallerResource = "WSGM.DeviceLab.PawnIO.setup.exe";
    private static readonly TimeSpan InstallerDeadline = TimeSpan.FromMinutes(3);

    private static readonly Lazy<PawnIoPin> LazyPin = new(ReadPin);

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

    /// <summary>Installs the pinned version.</summary>
    /// <param name="cancellationToken">Cancels the wait; the installer itself is not interrupted.</param>
    /// <returns>Null on success, or the problem.</returns>
    public static Task<string?> InstallAsync(CancellationToken cancellationToken)
    {
        return RunInstallerAsync(Pin.InstallArguments, cancellationToken);
    }

    /// <summary>Uninstalls PawnIO with its own uninstaller.</summary>
    /// <param name="status">Current status, for the install location.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Null on success, or the problem.</returns>
    /// <remarks>The uninstaller must carry the pinned signature; it is held open while it runs.</remarks>
    public static async Task<string?> UninstallAsync(PawnIoStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.InstallLocation is not { Length: > 0 } location)
        {
            return "PawnIO's install location is unknown, so its uninstaller cannot be found.";
        }

        var uninstaller = Path.Combine(location, "uninstall.exe");
        if (!File.Exists(uninstaller))
        {
            return $"PawnIO's uninstaller is missing: {uninstaller}";
        }

        await using var held = new FileStream(uninstaller, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (AuthenticodeSignature.Verify(uninstaller, held.SafeFileHandle, Pin.SignerThumbprint) is { } problem)
        {
            return $"PawnIO's uninstaller is not signed as expected ({problem}); it was not run.";
        }

        return await RunAsync(uninstaller, Pin.UninstallArguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> RunInstallerAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (!InstallerBundled)
        {
            return "This build of Device Lab does not include the PawnIO installer.";
        }

        var directory = CreateAdministratorsOnlyDirectory();
        var installer = Path.Combine(directory, "PawnIO_setup.exe");
        try
        {
            await using (var resource = typeof(PawnIoSetup).Assembly.GetManifestResourceStream(InstallerResource)!)
            await using (var file = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await resource.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            // Held without write or delete sharing until the installer exits, so nothing can replace
            // the file between the checks and the run.
            await using var held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(held, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(digest, Pin.AssetSha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"The bundled PawnIO installer does not match its pin ({digest}); it was not run.";
            }

            if (AuthenticodeSignature.Verify(installer, held.SafeFileHandle, Pin.SignerThumbprint) is { } problem)
            {
                return $"The bundled PawnIO installer is not signed as expected ({problem}); it was not run.";
            }

            return await RunAsync(installer, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover copy of a signed public installer in an administrators-only folder is harmless.
            }
        }
    }

    // A random name directly under %SystemRoot%\Temp: users may create folders there but cannot
    // delete or rename one an administrator created, and the protected ACL keeps them out of it.
    internal static string CreateAdministratorsOnlyDirectory(string purpose = "pawnio")
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp",
            $"wsgm-device-lab-{purpose}-{Guid.NewGuid():N}");
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            throw new IOException($"The installer folder already exists: {path}");
        }

        directory.Create(security);
        return path;
    }

    private static async Task<string?> RunAsync(string executable, string[] arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(InstallerDeadline);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"{Path.GetFileName(executable)} did not finish within {InstallerDeadline.TotalMinutes:0} minutes.";
        }

        return process.ExitCode == 0 ? null : $"{Path.GetFileName(executable)} exited with code {process.ExitCode}.";
    }

    private static PawnIoPin ReadPin()
    {
        using var stream = typeof(PawnIoSetup).Assembly.GetManifestResourceStream(LockResource)
                           ?? throw new InvalidDataException("The PawnIO lock file is not embedded.");
        using var document = JsonDocument.Parse(stream);
        var component = document.RootElement.GetProperty("component");
        return new PawnIoPin
        {
            Version = component.GetProperty("version").GetString()!,
            AssetSha256 = component.GetProperty("assetSha256").GetString()!,
            SignerThumbprint = component.GetProperty("signerThumbprint").GetString()!,
            InstallArguments =
                [.. component.GetProperty("installArguments").EnumerateArray().Select(item => item.GetString()!)],
            UninstallArguments =
                [.. component.GetProperty("uninstallArguments").EnumerateArray().Select(item => item.GetString()!)],
            UninstallKey = component.GetProperty("uninstallKey").GetString()!,
            MinimumInstalledVersion = component.GetProperty("minimumInstalledVersion").GetString()!
        };
    }
}
