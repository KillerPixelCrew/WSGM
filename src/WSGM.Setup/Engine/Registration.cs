using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32;
using WSGM.Install;

namespace WSGM.Setup.Engine;

/// <summary>The Windows "Installed apps" entry, and the one left by WSGM 1.0's Inno installer.</summary>
internal static class Registration
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string EntryName = "WSGM";
    private const string RunOnceRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ResumeEntryName = "WSGMDriverUpdate";

    /// <summary>The AppId of the Inno installer every WSGM 1.0 release used.</summary>
    internal const string LegacyInnoEntry = "{E4C7A9D2-58F1-4B36-A2C4-7D9E31B0F5C8}_is1";

    /// <summary>The uninstall key of usbip-win2's Inno installer, from its fixed AppId.</summary>
    internal const string UsbipUninstallKey = "{199505b0-b93d-4521-a8c7-897818e0205a}_is1";

    /// <summary>The version this setup registered, or null when WSGM 2 is not installed.</summary>
    /// <returns>Parsed registered version, or null when absent or malformed; registry access failures propagate.</returns>
    public static Version? InstalledVersion()
    {
        using var machine = Machine();
        using var key = machine.OpenSubKey($@"{UninstallRoot}\{EntryName}");
        return key?.GetValue("DisplayVersion") is string text && Version.TryParse(text, out var version)
            ? version
            : null;
    }

    internal static void RestoreVersion(string? version)
    {
        if (version is null)
        {
            Unregister();
        }
        else
        {
            Register(version);
        }
    }

    /// <summary>Writes the uninstall entry. Uninstall and repair both run the installed setup copy.</summary>
    /// <param name="version">Display version to record for the installed payload.</param>
    public static void Register(string version)
    {
        using var machine = Machine();
        using var key = machine.CreateSubKey($@"{UninstallRoot}\{EntryName}");
        var setup = $"\"{InstallLayout.SetupExe}\"";
        key.SetValue("DisplayName", "WSGM");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "KillerPixelCrew");
        key.SetValue("URLInfoAbout", "https://github.com/KillerPixelCrew/WSGM");
        key.SetValue("InstallLocation", InstallLayout.Root);
        key.SetValue("DisplayIcon", InstallLayout.AppExe);
        key.SetValue("UninstallString", setup + " /uninstall");
        key.SetValue("QuietUninstallString", setup + " /quiet /uninstall");
        key.SetValue("ModifyPath", setup + " /repair");
        key.SetValue("NoModify", 0, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(DirectorySize(InstallLayout.Root) / 1024), RegistryValueKind.DWord);
    }

    /// <summary>Asks Windows to run the installed setup in <c>/finishdrivers</c> mode after the next restart.</summary>
    /// <returns><see langword="true" /> when the entry was written.</returns>
    /// <remarks>Windows deletes a RunOnce entry as it runs it, so an interrupted run cannot repeat forever.</remarks>
    public static bool ScheduleResumeAfterRestart()
    {
        try
        {
            using var machine = Machine();
            using var key = machine.CreateSubKey(RunOnceRoot);
            key.SetValue(ResumeEntryName, $"\"{InstallLayout.SetupExe}\" /finishdrivers");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException
                                       or IOException)
        {
            return false;
        }
    }

    /// <summary>Drops a scheduled resume that is no longer needed.</summary>
    public static void CancelResumeAfterRestart()
    {
        try
        {
            using var machine = Machine();
            using var key = machine.OpenSubKey(RunOnceRoot, true);
            key?.DeleteValue(ResumeEntryName, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException
                                       or IOException)
        {
            // A resume that cannot be removed runs once and finds nothing to do.
        }
    }

    /// <summary>Removes the uninstall entry.</summary>
    public static void Unregister()
    {
        using var machine = Machine();
        machine.DeleteSubKeyTree($@"{UninstallRoot}\{EntryName}", false);
    }

    /// <summary>The WSGM 1.0 uninstall command and version, or null when 1.0 is not installed.</summary>
    /// <returns>First matching legacy uninstall command and version, or null when absent.</returns>
    public static (string Command, string Version)? LegacyInstall()
    {
        using var machine = Machine();
        using var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        foreach (var root in new[] { machine, machine32, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey($@"{UninstallRoot}\{LegacyInnoEntry}");
            if (key?.GetValue("UninstallString") is string command)
            {
                return (command, key.GetValue("DisplayVersion") as string ?? "1.x");
            }
        }

        return null;
    }

    /// <summary>
    ///     Runs an Inno uninstaller silently and waits for its entry to disappear: Inno hands the work to a
    ///     copy of itself in a temporary folder, so the process it was started as exits early.
    /// </summary>
    /// <param name="command">The uninstall string.</param>
    /// <param name="stillInstalled">Whether the entry still exists.</param>
    /// <returns>Whether the entry is gone.</returns>
    public static bool RunInnoUninstaller(string command, Func<bool> stillInstalled)
    {
        var (file, arguments) = SplitCommand(command);
        WindowsSetup.Run(file, (arguments + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim());
        for (var second = 0; second < 180 && stillInstalled(); second++)
        {
            Thread.Sleep(1000);
        }

        return !stillInstalled();
    }

    /// <summary>
    ///     The uninstall entry of usbip-win2. Its Inno AppId fixes the key (AppGUID in usbip-win2's
    ///     userspace/innosetup/setup.iss); a display name match would also find Microsoft's usbipd-win.
    /// </summary>
    /// <returns>Registered uninstall command for the fixed USB/IP AppId, or null.</returns>
    public static string? FindUsbipUninstallCommand()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var entry = machine.OpenSubKey($@"{UninstallRoot}\{UsbipUninstallKey}");
            if (entry?.GetValue("UninstallString") is string command)
            {
                return command;
            }
        }

        return null;
    }

    /// <summary>The uninstall entry of an installed program whose display name contains the text.</summary>
    /// <param name="displayNameContains">Case-insensitive substring of the registered display name.</param>
    /// <returns>First matching uninstall command across machine registry views, or null.</returns>
    public static string? FindUninstallCommand(string displayNameContains)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = machine.OpenSubKey(UninstallRoot);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is string display
                    && display.Contains(displayNameContains, StringComparison.OrdinalIgnoreCase)
                    && entry.GetValue("UninstallString") is string command)
                {
                    return command;
                }
            }
        }

        return null;
    }

    internal static (string File, string Arguments) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end < 0 ? (command.Trim('"'), "") : (command[1..end], command[(end + 1)..].Trim());
        }

        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    private static RegistryKey Machine()
    {
        return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>Which system components setup installed itself and may therefore remove.</summary>
internal sealed record InstalledComponents
{
    /// <summary>Setup installed the USB/IP driver.</summary>
    public bool Usbip { get; init; }

    /// <summary>Setup installed HidHide.</summary>
    public bool HidHide { get; init; }

    public static InstalledComponents Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path),
                    SetupJsonContext.Default.InstalledComponents) ?? new InstalledComponents()
                : new InstalledComponents();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new InstalledComponents();
        }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(this, SetupJsonContext.Default.InstalledComponents));
    }

    /// <summary>Whether a USB/IP driver is installed, by whoever and of whatever vintage.</summary>
    /// <remarks>
    ///     Presence only, for the progress page. Installation separately checks the pinned version;
    ///     a present driver may still require an upgrade.
    /// </remarks>
    /// <returns>True for a registered USB/IP uninstaller or the conventional usbip.exe path; does not verify version.</returns>
    public static bool UsbipPresent()
    {
        return Registration.FindUsbipUninstallCommand() is not null
               || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip",
                   "usbip.exe"));
    }

    /// <summary>Whether HidHide is installed, by whoever.</summary>
    /// <returns>True when an installed-program display name containing HidHide has an uninstall command.</returns>
    public static bool HidHidePresent()
    {
        return Registration.FindUninstallCommand("HidHide") is not null;
    }
}

/// <summary>The result the USB/IP script publishes, because its exit code is always 0.</summary>
/// <param name="Outcome">installed, already-present, blocked-newer-version, report-only or failed.</param>
/// <param name="RebootRequired">Whether Windows must restart before the driver works.</param>
/// <param name="Detail">A line for the log and the summary.</param>
internal sealed record UsbipOutcome(string Outcome, bool RebootRequired, string Detail)
{
    public bool Succeeded => Outcome is "installed" or "already-present";

    /// <summary>Whether the pinned driver is not installed yet and a run would replace it.</summary>
    public bool UpdateRequired => Outcome is "update-required";

    /// <summary>Parses the schema-1 INI status written by the driver script.</summary>
    /// <param name="ini">Driver script status-file contents.</param>
    /// <returns>Parsed outcome, or a failed outcome for unsupported or incomplete status data.</returns>
    public static UsbipOutcome Parse(string ini)
    {
        var values = ini.Split('\n').Select(line => line.Trim())
            .Where(line => line.Contains('=') && !line.StartsWith('['))
            .Select(line => line.Split('=', 2))
            .GroupBy(pair => pair[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);

        string Value(string key, string fallback = "")
        {
            return values.TryGetValue(key, out var value) ? value : fallback;
        }

        if (Value("schemaVersion") != "1")
        {
            return new UsbipOutcome("failed", false, "The USB/IP driver returned an unsupported status format.");
        }

        var outcome = Value("outcome");
        var reboot = string.Equals(Value("rebootRequired", "false"), "true", StringComparison.OrdinalIgnoreCase);
        var message = Value("message");
        var detail = $"outcome={outcome}, required={Value("requiredVersion")}, observed={Value("observedVersion")}, "
                     + $"registered={Value("driverRegistered", "unknown")}, reboot={reboot}";
        return outcome switch
        {
            "installed" or "already-present" or "update-required" or "report-only" =>
                new UsbipOutcome(outcome, reboot, detail),
            "failed" or "blocked-newer-version" => new UsbipOutcome(outcome, reboot,
                (message.Length > 0 ? message : "The USB/IP driver was not made available.")
                + $" ({detail})"),
            _ => new UsbipOutcome("failed", reboot, $"The USB/IP driver returned an incomplete result ({detail}).")
        };
    }
}

/// <summary>Source-generated JSON metadata for setup's own files.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(InstalledComponents))]
internal sealed partial class SetupJsonContext : JsonSerializerContext;
