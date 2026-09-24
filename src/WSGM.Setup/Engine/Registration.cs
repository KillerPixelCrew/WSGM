using System;
using System.IO;
using System.Linq;
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

    /// <summary>The AppId of the Inno installer every WSGM 1.0 release used.</summary>
    internal const string LegacyInnoEntry = "{E4C7A9D2-58F1-4B36-A2C4-7D9E31B0F5C8}_is1";

    /// <summary>The version this setup registered, or null when WSGM 2 is not installed.</summary>
    public static Version? InstalledVersion()
    {
        using var key = Machine().OpenSubKey($@"{UninstallRoot}\{EntryName}");
        return key?.GetValue("DisplayVersion") is string text && Version.TryParse(text, out var version)
            ? version
            : null;
    }

    /// <summary>Writes the uninstall entry. Uninstall and repair both run the installed setup copy.</summary>
    public static void Register(string version)
    {
        using var key = Machine().CreateSubKey($@"{UninstallRoot}\{EntryName}");
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

    /// <summary>Removes the uninstall entry.</summary>
    public static void Unregister()
    {
        Machine().DeleteSubKeyTree($@"{UninstallRoot}\{EntryName}", false);
    }

    /// <summary>The WSGM 1.0 uninstall command and version, or null when 1.0 is not installed.</summary>
    public static (string Command, string Version)? LegacyInstall()
    {
        foreach (var root in new[]
                 {
                     Machine(), RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
                     Registry.CurrentUser
                 })
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

    /// <summary>The uninstall entry of an installed program whose display name contains the text.</summary>
    public static string? FindUninstallCommand(string displayNameContains)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var uninstall = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(UninstallRoot);
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

    public static InstalledComponents Read()
    {
        try
        {
            return File.Exists(InstallLayout.InstalledComponents)
                ? JsonSerializer.Deserialize(File.ReadAllText(InstallLayout.InstalledComponents),
                    SetupJsonContext.Default.InstalledComponents) ?? new InstalledComponents()
                : new InstalledComponents();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new InstalledComponents();
        }
    }

    public void Write()
    {
        Directory.CreateDirectory(InstallLayout.MachineData);
        File.WriteAllText(InstallLayout.InstalledComponents,
            JsonSerializer.Serialize(this, SetupJsonContext.Default.InstalledComponents));
    }

    /// <summary>Whether the USB/IP driver is installed, by whoever.</summary>
    public static bool UsbipPresent()
    {
        return Registration.FindUninstallCommand("USBip") is not null
               || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip",
                   "usbip.exe"));
    }

    /// <summary>Whether HidHide is installed, by whoever.</summary>
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

    /// <summary>Parses the schema-1 INI the script writes, as the Inno installer did.</summary>
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
            "installed" or "already-present" => new UsbipOutcome(outcome, reboot, detail),
            "failed" or "blocked-newer-version" => new UsbipOutcome(outcome, reboot,
                (message.Length > 0
                    ? message[..Math.Min(512, message.Length)]
                    : "The USB/IP driver was not made available.")
                + $" ({detail})"),
            _ => new UsbipOutcome("failed", reboot, $"The USB/IP driver returned an incomplete result ({detail}).")
        };
    }
}

/// <summary>Source-generated JSON metadata for setup's own files.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(InstalledComponents))]
internal sealed partial class SetupJsonContext : JsonSerializerContext;
