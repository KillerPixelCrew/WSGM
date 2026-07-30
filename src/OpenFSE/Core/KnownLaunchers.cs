using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace OpenFSE.Core;

/// <summary>A launcher OpenFSE knows how to drive: window/process matching, the
/// arguments that open its fullscreen UI, and how to find it on disk. Ported from
/// AnyFSE's launcher table (MIT) so users pick from a list instead of typing
/// executable paths and protocol URLs.</summary>
public sealed record LauncherPreset(
    string Name,
    string ExeName,
    string Args,
    string ProcessNames,
    string WindowClass,
    string ActivationProtocol,
    Func<string?> Locate,
    string DownloadUrl)
{
    /// <summary>Full path to the installed exe, or null when not installed.</summary>
    public string? InstalledPath => Locate();
}

public static class KnownLaunchers
{
    public static IReadOnlyList<LauncherPreset> All { get; } =
    [
        new("Steam Big Picture", "Steam.exe", "steam://open/bigpicture",
            "steam;steamwebhelper", "SDL_app", "steam://open/bigpicture",
            () => FromUninstallKey("Steam", "Steam.exe"),
            "https://store.steampowered.com/about/"),

        new("Playnite Fullscreen", "Playnite.FullscreenApp.exe", "--hidesplashscreen",
            "Playnite.FullscreenApp;Playnite.DesktopApp", "", "@",
            () => FromUninstallKey("Playnite", "Playnite.FullscreenApp.exe"),
            "https://playnite.link/"),

        new("Playnite Desktop", "Playnite.DesktopApp.exe", "--hidesplashscreen",
            "Playnite.DesktopApp;Playnite.FullscreenApp", "", "@",
            () => FromUninstallKey("Playnite", "Playnite.DesktopApp.exe"),
            "https://playnite.link/"),

        new("LaunchBox BigBox", "BigBox.exe", "",
            "BigBox", "", "",
            () => FromUninstallKey("LaunchBox", "BigBox.exe"),
            "https://www.launchbox-app.com/download"),

        new("RetroBat", "RetroBat.exe", "",
            "emulationstation;retrobat", "SDL_app", "",
            () => FromUninstallKey("RetroBat", "RetroBat.exe"),
            "https://www.retrobat.org/download/"),

        new("Kodi", "kodi.exe", "",
            "kodi", "Kodi", "",
            () => FromUninstallKey("Kodi", "kodi.exe"),
            "https://kodi.tv/download/windows/"),

        new("Razer Cortex", "RazerCortex.Shell.exe", "",
            "RazerCortex.Shell", "RazerCortexMainWnd", "",
            () => FromUninstallKey("Razer Cortex", "RazerCortex.Shell.exe"),
            "https://www.razer.com/cortex"),

        new("Armoury Crate SE", "asusac://", "",
            "ArmouryCrateSe", "Windows.UI.Core.CoreWindow", "asusac://",
            () => FromProtocol("asusac"),
            "https://armoury-crate.com/#download"),

        new("One Game Launcher", "ogl://", "",
            "OneGameLauncher", "Windows.UI.Core.CoreWindow", "ogl://",
            () => FromProtocol("ogl"),
            "https://ogl.app/"),
    ];

    /// <summary>Presets whose launcher is actually installed on this machine.</summary>
    public static List<LauncherPreset> Detected()
    {
        var result = new List<LauncherPreset>();
        foreach (var preset in All)
        {
            try
            {
                if (!string.IsNullOrEmpty(preset.InstalledPath))
                {
                    result.Add(preset);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Detection failed for {preset.Name}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Finds the preset matching an already-configured path (so an existing
    /// config selects the right entry in the UI).</summary>
    public static LauncherPreset? MatchByPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        foreach (var preset in All)
        {
            if (path.Contains("://"))
            {
                if (string.Equals(path, preset.ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    return preset;
                }
                continue;
            }
            if (string.Equals(Path.GetFileName(path), preset.ExeName, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }
        return null;
    }

    // --- detection helpers (registry only — no WinRT, AOT-friendly) ---

    /// <summary>Looks up an app's install location in the 32/64-bit uninstall keys
    /// (HKLM and HKCU) by display name, then appends the executable.</summary>
    private static string? FromUninstallKey(string displayNamePrefix, string exeName)
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in roots)
            {
                using var key = hive.OpenSubKey(root);
                if (key is null)
                {
                    continue;
                }
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("DisplayName") is not string display ||
                        !display.StartsWith(displayNamePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var location = sub.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(location))
                    {
                        // Fall back to the folder of the uninstaller.
                        var uninstall = (sub.GetValue("UninstallString") as string)?.Trim('"');
                        location = string.IsNullOrWhiteSpace(uninstall) ? null : Path.GetDirectoryName(uninstall);
                    }
                    if (string.IsNullOrWhiteSpace(location))
                    {
                        continue;
                    }

                    var candidate = Path.Combine(location.Trim('"'), exeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Store/UWP launchers are started by protocol, not by exe path. The
    /// registered protocol handler (HKCR) is both the proof it's installed and the
    /// thing we actually launch.</summary>
    private static string? FromProtocol(string protocol)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(protocol);
            return key?.GetValue("URL Protocol") is not null ? $"{protocol}://" : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Protocol detection failed for {protocol}: {ex.Message}");
            return null;
        }
    }
}
