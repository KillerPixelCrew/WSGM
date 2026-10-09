using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using WSGM.Security;

namespace WSGM.Setup.Engine;

/// <summary>Installs the pinned signed driver required by native handheld EC and CPU backends.</summary>
internal static class PawnIoInstaller
{
    internal static bool IsInstalled()
    {
        using var pin = ReadPin();
        var component = pin.RootElement.GetProperty("component");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(component.GetProperty("uninstallKey").GetString()!);
        return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version)
               && version >= Version.Parse(component.GetProperty("minimumInstalledVersion").GetString()!);
    }

    internal static bool Install(SetupStep step)
    {
        if (IsInstalled())
        {
            step.DoneLabel = "PawnIO already installed";
            return true;
        }

        using var pin = ReadPin();
        var component = pin.RootElement.GetProperty("component");
        using var resource = typeof(PawnIoInstaller).Assembly.GetManifestResourceStream("WSGM.PawnIO.Installer")
                             ?? throw new InvalidOperationException(
                                 "This setup does not carry the pinned PawnIO installer.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp", "wsgm-pawnio-" + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(directory).Create(security);
        var installer = Path.Combine(directory, "PawnIO_setup.exe");
        var running = false;
        try
        {
            using (var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
            }

            using var held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(SHA256.HashData(held));
            if (!string.Equals(digest, component.GetProperty("assetSha256").GetString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PawnIO installer digest does not match its pin.");
            }

            if (AuthenticodeSignature.Verify(installer, held.SafeFileHandle,
                    component.GetProperty("signerThumbprint").GetString()!) is { } problem)
            {
                throw new InvalidDataException("PawnIO installer signature was refused: " + problem);
            }

            var start = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in component.GetProperty("installArguments").EnumerateArray())
            {
                start.ArgumentList.Add(argument.GetString()!);
            }

            using var process = Process.Start(start) ??
                                throw new InvalidOperationException("PawnIO installer did not start.");
            running = true;
            if (!process.WaitForExit(180000))
            {
                // The installer owns its native work. Preserve its bytes and never launch another attempt.
                throw new TimeoutException("PawnIO installation is still running; no second installer was started.");
            }

            running = false;
            if (process.ExitCode is not (0 or 3010))
            {
                throw new InvalidOperationException($"PawnIO installation failed with exit code {process.ExitCode}.");
            }

            if (process.ExitCode == 3010)
            {
                step.Note = "PawnIO installation requires a restart.";
            }

            return true;
        }
        finally
        {
            if (!running)
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static JsonDocument ReadPin()
    {
        using var resource = typeof(PawnIoInstaller).Assembly.GetManifestResourceStream("WSGM.PawnIO.Pin")
                             ?? throw new InvalidOperationException("PawnIO pin is missing.");
        return JsonDocument.Parse(resource);
    }
}
