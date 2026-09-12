using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What a takeover attempt did.</summary>
/// <param name="Disabled">Sources this attempt turned off and recorded.</param>
/// <param name="Pending">Sources whose change could not be verified and stay recorded as pending.</param>
/// <param name="NeedsElevation">Sources left alone because this process cannot change them.</param>
public sealed record SteamAutostartTakeoverResult(
    IReadOnlyList<SteamAutostartSource> Disabled,
    IReadOnlyList<SteamAutostartSource> Pending,
    IReadOnlyList<SteamAutostartSource> NeedsElevation)
{
    /// <summary>Whether every source this attempt saw is now off.</summary>
    public bool Complete => Pending.Count == 0 && NeedsElevation.Count == 0;
}

/// <summary>Turns Steam's own startup entries off and puts them back exactly as they were.
///
/// WSGM starts Steam so the client inherits WSGM's integrity; a Steam that Windows already started
/// would defeat that without saying so. Nothing is deleted: each entry is disabled the way Task
/// Manager's Startup tab disables it, and the previous state is recorded before the write so the
/// uninstaller can restore it. An entry the user has since changed themselves is left alone.</summary>
public static class SteamAutostartTakeover
{
    /// <summary>Windows' own disabled marker: the flag byte, then a filetime it does not act on.</summary>
    private static byte[] DisabledApproval() =>
        [.. new byte[] { 3, 0, 0, 0, 0, 0, 0, 0 }, .. BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc())];

    /// <summary>Disables the given sources, recording each previous state in the configuration
    /// before the write so an interrupted takeover is still undoable.</summary>
    /// <param name="system">The startup surfaces to change.</param>
    /// <param name="sources">The sources to disable; already-disabled ones are skipped.</param>
    /// <param name="elevated">Whether this process can change machine-scope sources.</param>
    /// <param name="record">Persists one record; called before and after each write.</param>
    /// <returns>What was disabled, what stayed uncertain and what needs elevation.</returns>
    public static SteamAutostartTakeoverResult Disable(
        IAutostartSystem system,
        IEnumerable<SteamAutostartSource> sources,
        bool elevated,
        Action<SteamAutostartRecord> record)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(record);
        List<SteamAutostartSource> disabled = [], pending = [], blocked = [];
        foreach (SteamAutostartSource source in sources)
        {
            if (!source.Enabled) { continue; }
            if (source.NeedsElevation && !elevated) { blocked.Add(source); continue; }
            SteamAutostartRecord entry = new()
            {
                Kind = source.Kind,
                Scope = source.Scope,
                Location = source.Location,
                Name = source.Name,
                Wow64 = source.Wow64,
                Pending = true,
            };
            try
            {
                if (source.Kind is SteamAutostartKind.ScheduledTask)
                {
                    // Recorded before the write: an interrupted disable must still be restorable.
                    record(entry);
                    if (!system.SetTaskEnabled(source.Location, false) || system.IsTaskEnabled(source.Location))
                    {
                        pending.Add(source);
                        continue;
                    }
                }
                else
                {
                    string list = ListFor(source);
                    byte[]? previous = system.ReadApproval(source.Scope, list, source.Name);
                    entry.PreviousApproval = previous is null ? null : Convert.ToBase64String(previous);
                    entry.PreviousApprovalExists = previous is not null;
                    record(entry);
                    system.WriteApproval(source.Scope, list, source.Name, DisabledApproval());
                    byte[]? readback = system.ReadApproval(source.Scope, list, source.Name);
                    if (SteamAutostartScanner.ApprovalMeansEnabled(readback))
                    {
                        pending.Add(source);
                        continue;
                    }
                    entry.WrittenApproval = Convert.ToBase64String(readback!);
                }
                entry.Pending = false;
                record(entry);
                disabled.Add(source);
                Log.Info($"Steam autostart: disabled {source.Describe()}.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Steam autostart: disabling {source.Describe()} failed: {ex.Message}");
                pending.Add(source);
            }
        }
        return new(disabled, pending, blocked);
    }

    /// <summary>Puts back what <see cref="Disable"/> turned off, skipping anything that no longer
    /// carries WSGM's own change. The user's later decision always wins.</summary>
    /// <param name="system">The startup surfaces to change.</param>
    /// <param name="records">The recorded changes.</param>
    /// <param name="elevated">Whether this process can change machine-scope sources.</param>
    /// <returns>The records that were restored and can be dropped.</returns>
    public static IReadOnlyList<SteamAutostartRecord> Restore(
        IAutostartSystem system, IEnumerable<SteamAutostartRecord> records, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(records);
        List<SteamAutostartRecord> restored = [];
        foreach (SteamAutostartRecord entry in records)
        {
            if (entry.Scope is SteamAutostartScope.Machine && !elevated) { continue; }
            try
            {
                if (entry.Kind is SteamAutostartKind.ScheduledTask)
                {
                    if (system.IsTaskEnabled(entry.Location))
                    {
                        // Someone turned it back on already; there is nothing of WSGM's left here.
                        restored.Add(entry);
                        continue;
                    }
                    if (!system.SetTaskEnabled(entry.Location, true)) { continue; }
                }
                else
                {
                    string list = ListFor(entry.Kind, entry.Wow64);
                    byte[]? current = system.ReadApproval(entry.Scope, list, entry.Name);
                    // A pending record never confirmed its bytes, so it may only undo a state that
                    // is still disabled.
                    bool ours = entry.WrittenApproval is { } written
                        ? current is not null && Convert.ToBase64String(current) == written
                        : !SteamAutostartScanner.ApprovalMeansEnabled(current);
                    if (!ours)
                    {
                        restored.Add(entry);
                        continue;
                    }
                    system.WriteApproval(entry.Scope, list, entry.Name,
                        entry.PreviousApprovalExists && entry.PreviousApproval is { } previous
                            ? Convert.FromBase64String(previous)
                            : null);
                }
                restored.Add(entry);
                Log.Info($"Steam autostart: restored {entry.Kind} \"{entry.Name}\".");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Steam autostart: restoring {entry.Kind} \"{entry.Name}\" failed: {ex.Message}");
            }
        }
        return restored;
    }

    private static string ListFor(SteamAutostartSource source) => ListFor(source.Kind, source.Wow64);

    private static string ListFor(SteamAutostartKind kind, bool wow64) =>
        kind is SteamAutostartKind.StartupShortcut
            ? SteamAutostartScanner.StartupFolderList
            : wow64 ? SteamAutostartScanner.Run32List : SteamAutostartScanner.RunList;
}
