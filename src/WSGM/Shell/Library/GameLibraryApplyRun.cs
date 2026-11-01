using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     One apply run's working state: the shortcut writer and launcher it writes with, the account it
///     confirmed, Steam's library as read once and folded with the run's own writes, the records as the
///     run changes them, and what it has applied and reported so far.
/// </summary>
/// <param name="generation">The service run the apply belongs to.</param>
/// <param name="writer">The shortcut writer over the live client.</param>
/// <param name="launcher">The launcher a generated packaged entry points at.</param>
/// <param name="accountId">The Steam account the run confirmed before its first write.</param>
/// <param name="snapshot">Every shortcut Steam held when the run started.</param>
/// <param name="recorded">What WSGM recorded before the run.</param>
internal sealed class GameLibraryApplyRun(
    long generation,
    SteamShortcutWriter writer,
    string launcher,
    uint accountId,
    IReadOnlyList<ExistingShortcut> snapshot,
    IReadOnlyList<ImportedEntry> recorded)
{
    /// <summary>The service run the apply belongs to.</summary>
    internal long Generation { get; } = generation;

    /// <summary>The shortcut writer over the live client.</summary>
    internal SteamShortcutWriter Writer { get; } = writer;

    /// <summary>The launcher a generated packaged entry points at.</summary>
    internal string Launcher { get; } = launcher;

    /// <summary>The Steam account the run confirmed before its first write.</summary>
    internal uint AccountId { get; } = accountId;

    /// <summary>The shortcuts Steam holds, with each entry's own read and the run's writes folded in.</summary>
    internal List<ExistingShortcut> Existing { get; } = [.. snapshot];

    /// <summary>The records by source and key, as the run changes them.</summary>
    internal Dictionary<(string Source, string Key), ImportedEntry> Records { get; } = Index(recorded);

    /// <summary>What went wrong without stopping the run, in the order it came up.</summary>
    internal List<string> Problems { get; } = [];

    /// <summary>How many entries the run applied.</summary>
    internal int Applied { get; set; }

    /// <summary>Whether the run stopped at a write Steam did not confirm; nothing after it is applied.</summary>
    internal bool Stopped { get; set; }

    /// <summary>Replaces what the run knows about one shortcut.</summary>
    /// <param name="appId">The shortcut's app id.</param>
    /// <param name="shortcut">The shortcut as it is now, or null when Steam no longer has it.</param>
    internal void Replace(uint appId, ExistingShortcut? shortcut)
    {
        Existing.RemoveAll(existing => existing.AppId == appId);
        if (shortcut is not null)
        {
            Existing.Add(shortcut);
        }
    }

    /// <summary>The app ids other records already name, which an entry may not adopt.</summary>
    /// <param name="identity">The entry's source and key.</param>
    /// <returns>A new set.</returns>
    internal HashSet<uint> ClaimedByOthers((string Source, string Key) identity)
    {
        return Records
            .Where(pair => !ImportPlan.Identity.Equals(pair.Key, identity) && pair.Value.AppId > 0)
            .Select(pair => pair.Value.AppId).ToHashSet();
    }

    private static Dictionary<(string Source, string Key), ImportedEntry> Index(IReadOnlyList<ImportedEntry> recorded)
    {
        Dictionary<(string Source, string Key), ImportedEntry> records = new(ImportPlan.Identity);
        foreach (var record in recorded)
        {
            records[(record.Source, record.Key)] = record;
        }

        return records;
    }
}
