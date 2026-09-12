using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Applies the Steam autostart takeover against the live machine and the configuration.
///
/// Split from the pure scanner and takeover rules so those stay testable: this part owns the
/// registry surfaces, the elevation hand-off and the config writes.</summary>
public static class SteamAutostartService
{
    /// <summary>The one-shot that performs the machine-scope disables from an elevated instance.</summary>
    public const string DisableArgument = "--disable-steam-autostart";

    /// <summary>The one-shot that restores them, run by the elevated uninstaller.</summary>
    public const string RestoreArgument = "--restore-steam-autostart";

    /// <summary>Finds the startup sources that would launch Steam behind WSGM's back.</summary>
    /// <param name="system">The startup surfaces to read; the live ones by default.</param>
    /// <returns>The matching sources, enabled and disabled alike.</returns>
    public static IReadOnlyList<SteamAutostartSource> Scan(IAutostartSystem? system = null) =>
        SteamAutostartScanner.Scan(system ?? new AutostartSystem(), Steam.ExePath);

    /// <summary>Disables every enabled source, elevating once when a machine-scope source needs it.
    /// Records each change in the configuration before the write.</summary>
    /// <param name="sources">The scanned sources to act on.</param>
    /// <param name="allowElevation">Whether an elevation prompt is acceptable here. False at
    /// sign-in, where a prompt over the booting desktop would be hostile.</param>
    /// <param name="system">The startup surfaces to change; the live ones by default.</param>
    /// <returns>What this attempt achieved.</returns>
    public static SteamAutostartTakeoverResult Apply(
        IReadOnlyList<SteamAutostartSource> sources, bool allowElevation, IAutostartSystem? system = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        IAutostartSystem surfaces = system ?? new AutostartSystem();
        bool elevated = ElevationCheck.IsCurrentProcessElevated() is true;
        SteamAutostartTakeoverResult result = SteamAutostartTakeover.Disable(
            surfaces, sources, elevated, RecordDisabled);

        if (result.NeedsElevation.Count == 0 || elevated || !allowElevation)
        {
            if (result.NeedsElevation.Count != 0)
            {
                Log.Warn("Steam autostart: " + string.Join(", ", result.NeedsElevation.Select(s => s.Describe()))
                    + " needs an elevated WSGM and was left enabled.");
            }
            return result;
        }

        // One prompt for the whole machine scope. The elevated instance rescans and decides for
        // itself; no name from this side reaches its command line.
        if (!SelfElevation.RunElevatedAction(DisableArgument, "Steam autostart takeover"))
        {
            return result;
        }
        IReadOnlyList<SteamAutostartSource> remaining = SteamAutostartScanner.Scan(surfaces, Steam.ExePath)
            .Where(source => source.Enabled).ToArray();
        return result with
        {
            Disabled = [.. result.Disabled, .. result.NeedsElevation.Where(source =>
                !remaining.Any(other => other.Kind == source.Kind && other.Name == source.Name))],
            NeedsElevation = [.. result.NeedsElevation.Where(source =>
                remaining.Any(other => other.Kind == source.Kind && other.Name == source.Name))],
        };
    }

    /// <summary>Re-checks at a shell start once the takeover has been accepted, so a source that
    /// reappears is turned off again. Never prompts: a UAC dialog over a booting desktop is not an
    /// acceptable way to ask.</summary>
    public static void ReapplyAtStart()
    {
        try
        {
            if (!ConfigStore.Load().SteamAutostartTakeoverAccepted) { return; }
            IReadOnlyList<SteamAutostartSource> enabled = [.. Scan().Where(source => source.Enabled)];
            if (enabled.Count == 0) { return; }
            Log.Info($"Steam autostart: {enabled.Count} source(s) are enabled again; disabling them.");
            Apply(enabled, allowElevation: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart re-check failed: {ex.Message}");
        }
    }

    /// <summary>The elevated one-shot: rescans and disables what only an elevated process can.</summary>
    /// <returns>Zero when nothing that needs elevation is still enabled.</returns>
    public static int RunElevatedDisable()
    {
        try
        {
            IReadOnlyList<SteamAutostartSource> enabled =
                [.. Scan().Where(source => source.Enabled && source.NeedsElevation)];
            if (enabled.Count == 0) { return 0; }
            return SteamAutostartTakeover.Disable(new AutostartSystem(), enabled, elevated: true, RecordDisabled)
                .Complete ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Steam autostart takeover failed", ex);
            return 1;
        }
    }

    /// <summary>Puts back everything WSGM disabled. Run by the elevated uninstaller, and safe to
    /// run twice: a restored record is removed from the configuration.</summary>
    /// <returns>Zero when every record was handled.</returns>
    public static int RestoreAll()
    {
        try
        {
            AppConfig config = ConfigStore.Load();
            if (config.SteamAutostartDisabled.Count == 0) { return 0; }
            IReadOnlyList<SteamAutostartRecord> restored = SteamAutostartTakeover.Restore(
                new AutostartSystem(), config.SteamAutostartDisabled,
                ElevationCheck.IsCurrentProcessElevated() is true);
            HashSet<string> done = [.. restored.Select(Key)];
            ConfigStore.Mutate(current =>
            {
                current.SteamAutostartDisabled = [.. current.SteamAutostartDisabled.Where(entry => !done.Contains(Key(entry)))];
                if (current.SteamAutostartDisabled.Count == 0) { current.SteamAutostartTakeoverAccepted = false; }
            });
            return restored.Count == config.SteamAutostartDisabled.Count ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Steam autostart restore failed", ex);
            return 1;
        }
    }

    /// <summary>Records one change through a fresh read-modify-write, replacing any earlier record
    /// for the same entry so the first captured previous state is the one that survives.</summary>
    private static void RecordDisabled(SteamAutostartRecord entry)
    {
        try
        {
            ConfigStore.Mutate(config =>
            {
                SteamAutostartRecord? existing = config.SteamAutostartDisabled
                    .FirstOrDefault(other => Key(other) == Key(entry));
                if (existing is null)
                {
                    config.SteamAutostartDisabled.Add(entry);
                    return;
                }
                // A confirming write only adds the readback; the previous state was captured first
                // and must never be overwritten by a later, already-disabled observation.
                existing.Pending = entry.Pending;
                existing.WrittenApproval = entry.WrittenApproval ?? existing.WrittenApproval;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart: recording {entry.Kind} \"{entry.Name}\" failed: {ex.Message}");
        }
    }

    private static string Key(SteamAutostartRecord entry) =>
        $"{entry.Kind}|{entry.Scope}|{entry.Wow64}|{entry.Location}|{entry.Name}".ToLowerInvariant();
}
