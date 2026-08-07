using System;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Projects the current config into boot.json for the logon service
/// (WSGM-side only — the shared contract lives in Core\BootManifest). Called on
/// --setup, on settings saves that touch the inputs, and at every shell/boot
/// start so a stale Elevate/ExePath heals itself on the next session.</summary>
public static class BootManifestWriter
{
    /// <summary>Absolute path of the per-user boot manifest.</summary>
    public static string ManifestPath => Path.Combine(Log.Directory, BootManifestStore.FileName);

    /// <summary>Writes boot.json from <paramref name="config"/>. Best effort: a
    /// failed write only logs — the service then skips the next logon, which is
    /// recoverable, unlike a crashed setup/boot path.</summary>
    public static void WriteCurrent(AppConfig config)
    {
        try
        {
            var manifest = new BootManifest
            {
                GameModeBoot = config.GameModeBootEnabled,
                Elevate = config.StartupApps.Any(a => a.Enabled && a.Elevated) || Steam.RequiresElevatedShell,
                ExePath = Installer.IsAppInstalled
                    ? Installer.InstalledExePath
                    : Environment.ProcessPath ?? Installer.InstalledExePath,
            };
            BootManifestStore.Save(ManifestPath, manifest);
            Log.Info($"Boot manifest written: enabled={manifest.GameModeBoot} elevate={manifest.Elevate} exe={manifest.ExePath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Boot manifest write failed: {ex.Message}");
        }
    }

    /// <summary>Rewrites boot.json with game-mode boot force-disabled, keeping the
    /// rest current. Used by the crash-loop breaker so the next sign-in is a plain
    /// desktop even when config.json cannot be saved.</summary>
    public static void WriteDisabled(AppConfig config)
    {
        config.GameModeBootEnabled = false;
        WriteCurrent(config);
    }
}
