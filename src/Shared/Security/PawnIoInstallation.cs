using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WSGM.Security;

/// <summary>The result of one optional driver operation. An uncertain operation is never retried.</summary>
internal sealed record DriverInstallResult(bool Succeeded, bool Installed, bool RestartRequired, string? Error = null);

/// <summary>The shared pinned, signed PawnIO installation mechanism used by setup and Device Lab.</summary>
internal sealed class PawnIoInstallation(Assembly assembly, string pinResource, string installerResource)
{
    private readonly JsonDocument _pin = ReadPin(assembly, pinResource);
    private JsonElement Component => _pin.RootElement.GetProperty("component");
    internal bool InstallerBundled => assembly.GetManifestResourceInfo(installerResource) is not null;
    internal string? InstalledVersion => ReadRegistration("DisplayVersion");
    internal string? InstallLocation => ReadRegistration("InstallLocation");

    internal bool IsInstalled => Version.TryParse(InstalledVersion, out var version)
                                 && version >=
                                 Version.Parse(Component.GetProperty("minimumInstalledVersion").GetString()!);

    internal async Task<DriverInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            return new DriverInstallResult(true, false, false);
        }

        if (!InstallerBundled)
        {
            return new DriverInstallResult(false, false, false, "This build does not include the PawnIO installer.");
        }

        var directory = CreateAdministratorsOnlyDirectory("pawnio");
        var installer = Path.Combine(directory, "PawnIO_setup.exe");
        var completed = true;
        try
        {
            await using (var resource = assembly.GetManifestResourceStream(installerResource)!)
            await using (var file = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await resource.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            await using var held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(held, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(digest, Component.GetProperty("assetSha256").GetString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new DriverInstallResult(false, false, false, "PawnIO installer digest does not match its pin.");
            }

            if (AuthenticodeSignature.Verify(installer, held.SafeFileHandle,
                    Component.GetProperty("signerThumbprint").GetString()!) is { } problem)
            {
                return new DriverInstallResult(false, false, false,
                    "PawnIO installer signature was refused: " + problem);
            }

            var result = await RunAsync(installer, "installArguments", cancellationToken).ConfigureAwait(false);
            completed = result.Completed;
            return result.Result;
        }
        finally
        {
            if (completed)
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Preserve a signed public installer if Windows still holds it.
                }
            }
        }
    }

    internal async Task<DriverInstallResult> UninstallAsync(CancellationToken cancellationToken)
    {
        if (InstallLocation is not { Length: > 0 } location)
        {
            return new DriverInstallResult(false, false, false, "PawnIO's install location is unknown.");
        }

        var uninstaller = Path.Combine(location, "uninstall.exe");
        await using var held = new FileStream(uninstaller, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (AuthenticodeSignature.Verify(uninstaller, held.SafeFileHandle,
                Component.GetProperty("signerThumbprint").GetString()!) is { } problem)
        {
            return new DriverInstallResult(false, false, false, "PawnIO uninstaller signature was refused: " + problem);
        }

        return (await RunAsync(uninstaller, "uninstallArguments", cancellationToken).ConfigureAwait(false)).Result;
    }

    internal static string CreateAdministratorsOnlyDirectory(string purpose)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp",
            $"wsgm-{purpose}-{Guid.NewGuid():N}");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(path).Create(security);
        return path;
    }

    private async Task<(DriverInstallResult Result, bool Completed)> RunAsync(string executable,
        string argumentProperty,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in Component.GetProperty(argumentProperty).EnumerateArray())
        {
            start.ArgumentList.Add(argument.GetString()!);
        }

        using var process = Process.Start(start) ??
                            throw new InvalidOperationException("PawnIO installer did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (new DriverInstallResult(false, false, false,
                "PawnIO installation is still running; no second installer was started."), false);
        }

        var success = process.ExitCode is 0 or 3010;
        return (new DriverInstallResult(success, success, process.ExitCode == 3010,
            success ? null : $"PawnIO installer exited with code {process.ExitCode}."), true);
    }

    private string? ReadRegistration(string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Component.GetProperty("uninstallKey").GetString()!);
        return key?.GetValue(name) as string;
    }

    private static JsonDocument ReadPin(Assembly assembly, string resourceName)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName)
                             ?? throw new InvalidOperationException("PawnIO pin is missing.");
        return JsonDocument.Parse(resource);
    }
}
