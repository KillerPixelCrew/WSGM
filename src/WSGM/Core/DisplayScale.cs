using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Owns WSGM's per-monitor scaling policy (the 100%/125%/150% setting), using
///     <see cref="DisplayScaling" /> for the Windows CCD reads and writes. Changes apply INSTANTLY
///     with no logoff (live-verified) and PERSIST in the registry. Scaling
///     persistence is why the pre-game values are stored in WSGM's config and restored on desktop
///     mode, clean exit, panic, and recovery. Game mode runs at 100% so DPI-unaware games render
///     1:1 on the panel. HDR is queried and set on the path TARGET, never the GDI source; see
///     docs\power-and-display.md. A configured Game Mode layout owns resolution, position, refresh,
///     per-display scaling and HDR instead, through <see cref="WindowsDeviceControl.DisplayLayouts" />;
///     this class remains the scaling posture Default entry applies and the snapshot recovery
///     restores.
/// </summary>
public static class DisplayScale
{
    /// <summary>
    ///     Game mode: capture ALL current per-display scalings into the config
    ///     (unless a crashed session already left captured values there), persist them,
    ///     and only then drop every display to 100% — capture-then-set ordering so a
    ///     crash between the two can never lose the originals. When the save fails,
    ///     scaling is left untouched.
    /// </summary>
    public static void ApplyGameMode(AppConfig config)
    {
        var sources = GetActiveSources();
        if (sources.Count == 0)
        {
            Log.Warn("Display scale: no active display sources found.");
            return;
        }

        var freshCapture = config.SavedDisplayScaleEntries.Count == 0;
        var captured = new List<DisplayScaleEntry>();
        var toLower = new List<(ActiveDisplayPath Display, int Current)>();
        foreach (var source in sources)
        {
            if (!DisplayScaling.TryRead(source.Target, out var current) || current == 100)
            {
                continue;
            }

            var name = source.SourceName;
            if (name.Length == 0)
            {
                // A ""-named entry can never be matched by name on restore, so it
                // would sit in the config forever, re-logged on every restore.
                // Leave this display's scaling untouched rather than lower a value
                // we could not identify for restore.
                Log.Warn(
                    $"Display scale: device name query failed for a display at {current}% — leaving it unchanged.");
                continue;
            }

            captured.Add(new DisplayScaleEntry { DeviceName = name, Percent = (int)current });
            // A surviving snapshot means the previous game-mode session did not
            // finish restoring every display.  A dock/undock can expose a different
            // active source before recovery runs; never force that new display to
            // 100% without first owning its desktop-scale snapshot.  Existing named
            // entries are safe to lower again because their original value is still
            // recoverable.
            if (ShouldLowerDisplay(freshCapture, config.SavedDisplayScaleEntries, name))
            {
                toLower.Add((source, current));
            }
        }

        if (freshCapture && captured.Count > 0)
        {
            try
            {
                PersistScaleEntries(captured);
                config.SavedDisplayScaleEntries = captured;
            }
            catch (Exception ex)
            {
                Log.Warn($"Display scale: could not persist saved values — leaving scaling unchanged: {ex.Message}");
                return;
            }
        }

        foreach (var (source, current) in toLower)
        {
            if (TrySetScale(source.Target, 100))
            {
                Log.Info($"Display scale -> 100% (was {current}%).");
            }
        }
    }

    /// <summary>
    ///     Recovery path for clean exit, panic, uninstall and shell repair. Restores any
    ///     pending scaling snapshot; the layout a Game Mode session owes the desktop is separate and
    ///     belongs to <see cref="GameModeLaunchRecovery" />.
    /// </summary>
    public static void RestoreSaved(AppConfig config)
    {
        RestoreDpiSnapshot(config);
    }

    /// <summary>Handles an intentional transition into desktop mode.</summary>
    public static void ApplyDesktopMode(AppConfig config)
    {
        RestoreDpiSnapshot(config);
    }

    private static void RestoreDpiSnapshot(AppConfig config)
    {
        if (config.SavedDisplayScaleEntries.Count == 0)
        {
            return;
        }

        var sources = GetActiveSources();
        if (sources.Count == 0)
        {
            Log.Warn("Display scale: no active display sources — keeping saved values for a later restore.");
            return;
        }

        var named = sources
            .Select(source => (Source: source, Name: source.SourceName))
            .ToList();

        var remaining = new List<DisplayScaleEntry>();
        var positional = 0; // next active source for ""-named entries
        foreach (var entry in config.SavedDisplayScaleEntries)
        {
            if (entry.Percent is not (>= 100 and <= 500))
            {
                continue; // garbage value — dropping it is the only safe move
            }

            if (string.IsNullOrEmpty(entry.DeviceName))
            {
                // Written by an older build whose name query failed: "" can never
                // match by name, so pair it positionally by enumeration order —
                // and never re-save it, or it would block in the config forever,
                // warned about on every restore.
                if (positional < named.Count &&
                    TrySetScale(named[positional].Source.Target, entry.Percent))
                {
                    Log.Info(
                        $"Display scale restored to {entry.Percent}% (unnamed legacy entry, positional -> '{named[positional].Name}').");
                }
                else
                {
                    Log.Warn($"Display scale: dropping unmatchable unnamed entry ({entry.Percent}%).");
                }

                positional++;
                continue;
            }

            var idx = named.FindIndex(s => string.Equals(s.Name, entry.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                Log.Warn(
                    $"Display scale: display '{entry.DeviceName}' not active — keeping {entry.Percent}% for a later restore.");
                remaining.Add(entry);
                continue;
            }

            if (TrySetScale(named[idx].Source.Target, entry.Percent))
            {
                Log.Info($"Display scale restored to {entry.Percent}% ({entry.DeviceName}).");
            }
            else
            {
                remaining.Add(entry); // transient set failure — retry on the next restore path
            }
        }

        config.SavedDisplayScaleEntries = remaining;
        try
        {
            PersistScaleEntries(remaining);
        }
        catch (Exception ex)
        {
            Log.Warn($"Display scale: could not persist restore: {ex.Message}");
        }
    }

    /// <summary>
    ///     Persists ONLY the display-scale snapshot, through the config store's
    ///     read-modify-write path. Callers hand in a LONG-LIVED <see cref="AppConfig" /> —
    ///     the shell session's, on every game/desktop transition — and saving that whole
    ///     object would overwrite every field another process persisted since it was
    ///     loaded (see ConfigStore's contract), so a mode switch could silently revert
    ///     settings the user had just saved. Callers mirror the same values onto their own
    ///     instance so it stays in step with what went to disk.
    /// </summary>
    /// <param name="entries">The scale entries to persist (empty clears the snapshot).</param>
    private static void PersistScaleEntries(List<DisplayScaleEntry> entries)
    {
        ConfigStore.Mutate(fresh => fresh.SavedDisplayScaleEntries = entries);
    }

    /// <summary>
    ///     The display-scale percent WSGM's own UI should render at. Game
    ///     mode forces every display to 100%, which makes DIP-sized WSGM surfaces
    ///     physically tiny on dense handheld panels — so the overlay/taskbar upscale
    ///     themselves to the user's DESKTOP scaling: the pre-game-mode snapshot when
    ///     one exists, otherwise the panel's Windows-recommended scale (the snapshot
    ///     only captures displays that weren't already at 100%). Returns 100 when
    ///     nothing better is known.
    /// </summary>
    /// <param name="config">The configuration holding the pre-game-mode scale snapshot.</param>
    public static uint GetUiScalePercent(AppConfig config)
    {
        try
        {
            var sources = GetActiveSources();
            foreach (var saved in sources
                         .Select(source => source.SourceName)
                         .Select(name => config.SavedDisplayScaleEntries.Find(e =>
                             string.Equals(e.DeviceName, name, StringComparison.OrdinalIgnoreCase))))
            {
                if (saved is { Percent: >= 100 and <= 500 })
                {
                    return (uint)saved.Percent;
                }
            }

            if (sources.Count > 0 &&
                DisplayScaling.TryReadRange(sources[0].Target, out var current, out var recommended, out _))
            {
                return PickUiScalePercent(null, (uint)current, (uint)recommended);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Display scale: UI scale query failed: {ex.Message}");
        }

        return 100;
    }

    /// <summary>
    ///     The pure UI-scale decision: a valid saved desktop percent wins;
    ///     otherwise the larger of the current and Windows-recommended scale (in game
    ///     mode current is the forced 100, so recommended carries panels whose
    ///     desktop already ran at 100%).
    /// </summary>
    /// <param name="savedPercent">The pre-game-mode snapshot value, or null.</param>
    /// <param name="currentPercent">The display's current scale percent.</param>
    /// <param name="recommendedPercent">The display's Windows-recommended percent.</param>
    public static uint PickUiScalePercent(int? savedPercent, uint currentPercent, uint recommendedPercent)
    {
        return savedPercent is >= 100 and <= 500
            ? (uint)savedPercent
            : Math.Max(Math.Max(currentPercent, recommendedPercent), 100);
    }

    /// <summary>
    ///     Decides whether a display may be lowered after its scale was read.
    ///     A fresh capture owns every successfully identified value; during crash
    ///     recovery only a source already present in the durable snapshot is owned.
    /// </summary>
    /// <param name="freshCapture">Whether no earlier recovery snapshot exists.</param>
    /// <param name="savedEntries">The durable, device-keyed recovery snapshot.</param>
    /// <param name="deviceName">The active source's GDI device name.</param>
    internal static bool ShouldLowerDisplay(
        bool freshCapture,
        IReadOnlyList<DisplayScaleEntry> savedEntries,
        string deviceName)
    {
        return freshCapture || savedEntries.Any(entry =>
            string.Equals(entry.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    internal static int NormalizeConfiguredPercent(int percent)
    {
        return DisplayScaling.Snap(percent);
    }

    private static bool TrySetScale(DisplayTargetIdentity target, int percent)
    {
        var ok = DisplayScaling.TrySet(target, percent, out var detail);
        if (!ok)
        {
            Log.Warn($"Display scale: set {percent}% failed: {detail}.");
        }

        return ok;
    }

    private static List<ActiveDisplayPath> GetActiveSources()
    {
        try
        {
            return DisplayTopology.CaptureActive().Paths
                .DistinctBy(path => path.SourceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Display scale: enumeration failed: {ex.Message}");
            return [];
        }
    }
}
