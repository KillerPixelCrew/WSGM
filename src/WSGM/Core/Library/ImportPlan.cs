using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Works out what a sync would do, without doing any of it.</summary>
/// <remarks>
///     <para>
///         Pure. A scan is a dry run by construction: this produces the preview and nothing else
///         writes until the user applies it.
///     </para>
///     <para>
///         Identity is the pair of source and key, never the display name. Two titles can share a
///         name, and two sources could in principle share a key; no two titles share both.
///     </para>
///     <para>
///         The user's own choices are not an input here. An imported title's mode comes from its
///         record, which is what Steam actually has; the caller lays the user's choices over the
///         result, so a choice that differs from the record shows up as the change it is.
///     </para>
/// </remarks>
public static class ImportPlan
{
    private const string EditedByHand = "This entry has been changed by hand, so it is left alone.";

    private const string NeverConfirmed =
        "An earlier import of this title was never confirmed by Steam and may still appear. "
        + "Check the library before adding it again.";

    private const string AlreadyImported = "Already imported.";

    private const string AlreadyInSteam = "Steam already has an entry for this title.";

    private const string ClaimedByAnotherTitle =
        "Another imported title uses Steam's entry for this game. Add this title as a separate entry.";

    private const string DeletedFromSteam =
        "You deleted this title's shortcut from Steam. Tick it to add it again.";

    private const string CommandChanged =
        "What this title's shortcut should run has changed, so the shortcut is rewritten.";

    private const string TrackedStorage =
        "This game is on removable storage. Its shortcut now checks that storage before starting the game, "
        + "so Steam shows it unavailable while the storage is disconnected.";

    private const string StorageDisconnected =
        "Its storage is disconnected, so its source cannot list it. The shortcut and artwork stay; connect "
        + "the storage and scan again.";

    private const string FilesMissing =
        "Game files missing. Apply removes its Steam shortcut; WSGM never removes it on its own.";

    /// <summary>Compares titles by both halves of their identity, the way the plan does.</summary>
    public static IEqualityComparer<(string Source, string Key)> Identity => IdentityComparer.Instance;

    /// <summary>Builds the preview.</summary>
    /// <param name="discovered">What the sources found.</param>
    /// <param name="recorded">What WSGM remembers creating.</param>
    /// <param name="existing">The non-Steam shortcuts Steam currently has.</param>
    /// <param name="launcherTarget">The Target a generated entry would carry, or empty without a launcher.</param>
    /// <param name="defaultMode">The mode to use when nothing else decides.</param>
    /// <param name="includeUnroutable">Whether to offer titles with no validated launch route.</param>
    /// <param name="sources">
    ///     What this scan knows about a record's source (<see cref="ImportSourceState" />). Null means
    ///     every source was read.
    /// </param>
    /// <param name="shortcutState">
    ///     The availability tracker's latest state for a shortcut by app id, or null when it has none. A title
    ///     no source listed is kept while its storage is missing and offered for removal otherwise.
    /// </param>
    /// <returns>One entry per title, in discovery order, then removals.</returns>
    public static IReadOnlyList<ImportPlanEntry> Build(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnroutable,
        Func<string, ImportSourceState>? sources = null,
        Func<uint, ContentAvailability?>? shortcutState = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(launcherTarget);

        // Steam should never hold two entries with one id; if a read ever reports that, the first
        // stands rather than the whole scan failing on a duplicate key.
        Dictionary<uint, ExistingShortcut> byId = [];
        foreach (var shortcut in existing)
        {
            byId.TryAdd(shortcut.AppId, shortcut);
        }

        Dictionary<(string Source, string Key), ImportedEntry> records = new(IdentityComparer.Instance);
        foreach (var record in recorded)
        {
            records[(record.Source, record.Key)] = record;
        }

        Dictionary<string, (string Source, string Key)> contentOwners = new(StringComparer.Ordinal);
        foreach (var record in records.Values)
        {
            if (ContentIdentity(record.Content) is { } content)
            {
                contentOwners.TryAdd(content, (record.Source, record.Key));
            }
        }

        // A shortcut a record names belongs to that record's title. Offering it to another title as
        // an adoption would let one run adopt it and then delete it as the other title's removal.
        HashSet<uint> claimed = [.. records.Values.Where(record => record.AppId > 0).Select(record => record.AppId)];
        Context context = new(byId, existing, claimed, launcherTarget, defaultMode, includeUnroutable);
        var duplicateNames = discovered.Where(game => game.Content?.SourceKind == LibrarySourceKind.Rom)
            .GroupBy(game => (game.Content!.SystemId.ToUpperInvariant(), RomMetadata.CleanTitle(game.Name).ToUpperInvariant()))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(game => (game.SourceId, game.Key)))
            .ToHashSet(IdentityComparer.Instance);

        List<ImportPlanEntry> plan = [];
        HashSet<(string Source, string Key)> seen = new(IdentityComparer.Instance);
        foreach (var game in discovered)
        {
            if (!seen.Add((game.SourceId, game.Key)))
            {
                continue;
            }

            var record = records.GetValueOrDefault((game.SourceId, game.Key));
            var unresolvedHere = record is { HasUnresolvedLegacyMapping: true }
                && record.SteamMappings.Any(mapping => mapping.AccountId is null
                    && mapping.AppId > 0 && byId.ContainsKey(mapping.AppId));
            if (record is { AccountUnknown: true } || unresolvedHere)
            {
                plan.Add(new ImportPlanEntry(game.SourceId, game.Key, game.Name,
                    unresolvedHere ? ImportAction.Conflict : ImportAction.Skip,
                    unresolvedHere
                        ? "This legacy shortcut was edited in Steam. Restore its recorded command and scan again before changing it."
                        : "Waiting for an exact Steam account observation before changing this title.",
                    defaultMode, false, false, 0, false));
                continue;
            }

            // Inventory exists independently of whether this account imported it.
            if (record is { AccountId: not null, HasSteamMapping: false })
            {
                record = null;
            }
            if (ContentIdentity(game.Content) is { } contentIdentity)
            {
                if (contentOwners.TryGetValue(contentIdentity, out var owner)
                    && !IdentityComparer.Instance.Equals(owner, (game.SourceId, game.Key)))
                {
                    plan.Add(new ImportPlanEntry(game.SourceId, game.Key, game.Name, ImportAction.Conflict,
                        "This content and launch command are already owned by another configured source. Keep one source or explicitly clean up its old shortcut.",
                        ImportMode.SteamIntegration, false, false, record?.AppId ?? 0, false, Route: "managed"));
                    continue;
                }

                contentOwners[contentIdentity] = (game.SourceId, game.Key);
            }

            // Something that is not a game is left out, unless WSGM already imported it: then it is
            // handled like any other title, because dropping it here would offer an installed
            // title's shortcut for removal whenever the lookup that classifies it failed.
            if (!game.IsGame && record is null)
            {
                continue;
            }

            if ((game.Packaged ? Describe(game, record, context) : DescribeCommand(game, record, context))
                is { } entry)
            {
                if (records.GetValueOrDefault((game.SourceId, game.Key)) is { HasUnresolvedLegacyMapping: true } legacy
                    && entry.Action == ImportAction.Add)
                {
                    entry = entry with
                    {
                        Reason = legacy.SteamMappings.Any(mapping => mapping.AccountId is null && mapping.AppId == 0)
                            ? "The earlier shortcut creation was never confirmed. Check Steam, then select this title only if you want to retry it manually."
                            : "This title is not imported in the current account. Select it to add it here; unresolved receipts are preserved.",
                        Unconfirmed = legacy.SteamMappings.Any(mapping => mapping.AccountId is null && mapping.AppId == 0),
                        Preselect = false
                    };
                }

                if (record is null && entry.Action == ImportAction.Add && duplicateNames.Contains((game.SourceId, game.Key)))
                {
                    entry = entry with
                    {
                        Reason = "Multiple copies or versions found. Choose the file you want to import. " + game.Key,
                        Preselect = false
                    };
                }

                if (game.Content?.SystemId == "switch"
                    && Path.GetExtension(game.InstallPath).ToLowerInvariant() is ".nsz" or ".xcz")
                {
                    entry = entry with
                    {
                        Reason = "Decompress this NSZ/XCZ file to NSP/XCI before importing it.",
                        Selectable = false,
                        Preselect = false
                    };
                }

                plan.Add(entry);
                if (entry.Action == ImportAction.Adopt)
                {
                    context.Claim(entry.AppId);
                }
            }
        }

        // Removals last, and only for entries WSGM itself created whose title is gone.
        foreach (var record in records.Values)
        {
            if (seen.Contains((record.Source, record.Key)) || record.AppId == 0
                || record.AccountUnknown || record.HasUnresolvedLegacyMapping)
            {
                continue;
            }

            var state = sources?.Invoke(record.Source) ?? ImportSourceState.Read;
            if (record.RemovedFromSteam)
            {
                plan.Add(Removal(record, ImportAction.Skip,
                    "You removed this shortcut from Steam. Connect its storage and scan again to add it explicitly.",
                    false) with { AppId = 0 });
                continue;
            }

            // A source cannot list a title whose storage is gone; that is not an uninstall.
            var tracked = shortcutState?.Invoke(record.AppId);
            if (tracked == ContentAvailability.LibraryMissing)
            {
                plan.Add(Removal(record, ImportAction.Skip, StorageDisconnected, false));
                continue;
            }

            if (state is ImportSourceState.Unread)
            {
                continue;
            }

            if (!byId.TryGetValue(record.AppId, out var shortcut))
            {
                // Already gone from Steam: there is nothing to delete, but the record and the
                // controller override it left behind are still here. Kept as a removal, keeping the
                // app id so that override can be found, and selectable: an entry nobody can tick
                // is a record that announces on every scan that it is about to be dropped, forever.
                plan.Add(Removal(record, ImportAction.Remove,
                    "This entry is no longer in Steam, so only its record is left to drop.", true));
                continue;
            }

            // Agreement before anything is deleted: the live entry's Target and arguments must still
            // be exactly what the record says WSGM wrote.
            if (!OwnsRecorded(shortcut, record))
            {
                plan.Add(Removal(record, ImportAction.Conflict, EditedByHand, false));
                continue;
            }

            // Selectable, but never ticked for the user: deleting somebody's shortcut is a thing they
            // ask for, and an entry they cannot tick is one they can never ask for.
            plan.Add(Removal(record, ImportAction.Remove,
                tracked == ContentAvailability.FileMissing ? FilesMissing
                : state is ImportSourceState.Gone
                    ? "The launcher or folder this title came from is no longer on this machine."
                    : "This title is no longer installed.",
                true));
        }

        return plan;
    }

    private static string? ContentIdentity(ManagedContentRecord? content)
    {
        if (content is null || content.SourceKind == LibrarySourceKind.Launcher)
        {
            return null;
        }

        static string PathKey(ManagedContentPath path)
        {
            return (path.VolumeId.Length > 0
                ? path.VolumeId + "\u001f" + path.RelativePath
                : path.AbsolutePath).ToUpperInvariant();
        }

        var backing = PathKey(content.BackingPath);
        return content.SourceKind == LibrarySourceKind.Rom
            ? "rom\u001f" + EmulatorStorage.NormalizeSystemId(content.SystemId) + "\u001f" + backing
            : "command\u001f" + backing + "\u001f" + PathKey(content.Program) + "\u001f" + content.RawArguments;
    }

    /// <summary>Whether a live shortcut is one WSGM created for a discovered title.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="game">The title.</param>
    /// <param name="launcherTarget">The Target a generated packaged entry carries.</param>
    /// <returns>
    ///     For a packaged title, the launcher Target with the title's key in the arguments; for a
    ///     command title, exactly one of its routes.
    /// </returns>
    public static bool Owns(ExistingShortcut shortcut, DiscoveredGame game, string launcherTarget)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.Packaged
            ? PackagedLauncherShortcut.Owns(shortcut, launcherTarget, game.Key)
            : CommandShortcut.RouteOf(shortcut, game.CommandRoutes, launcherTarget) is not null;
    }

    /// <summary>Whether a live shortcut is still exactly what a record says WSGM wrote.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="record">What WSGM wrote.</param>
    /// <returns>True when its Target and arguments are still the recorded ones.</returns>
    /// <remarks>
    ///     The record, not the current launcher path, is the reference for both kinds of title. A
    ///     packaged shortcut the user edited keeps WSGM's Target and key and is still not WSGM's to
    ///     delete, and one written before WSGM moved folders is still WSGM's own.
    /// </remarks>
    public static bool OwnsRecorded(ExistingShortcut shortcut, ImportedEntry record)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        ArgumentNullException.ThrowIfNull(record);
        return CommandShortcut.Same(shortcut, record.Target, record.LaunchOptions)
               && CommandShortcut.SameFolder(shortcut.StartDirectory, record.StartDirectory);
    }

    /// <summary>Describes a title that launches through the packaged launcher.</summary>
    /// <returns>Its plan entry, or null for a title without a validated route that is not offered.</returns>
    private static ImportPlanEntry? Describe(DiscoveredGame game, ImportedEntry? record, Context context)
    {
        // The overlay route needs a validated launch route. Without one there is nothing for the
        // user to accept a risk about, so the choice is not offered rather than offered and refused.
        var canIntegrate = game.Launch.Validated;
        var requiresAcknowledgement = canIntegrate && game.Multiplayer is MultiplayerVerdict.Multiplayer;
        var mode = canIntegrate && game.Multiplayer is not MultiplayerVerdict.Multiplayer
            ? context.DefaultMode
            : ImportMode.ControllerOnly;

        // Not offered at all, rather than listed as something already done: a title nobody imported
        // is not "already imported" because it cannot be launched.
        if (!canIntegrate && !context.IncludeUnroutable && record is null)
        {
            return null;
        }

        ImportPlanEntry Entry(ImportAction action, string reason, ImportMode entryMode, uint appId, bool selectable,
            bool preselect = false)
        {
            return new ImportPlanEntry(game.SourceId, game.Key, game.Name, action, reason, entryMode,
                canIntegrate, requiresAcknowledgement, appId, selectable, Preselect: preselect);
        }

        if (record is { AppId: > 0 } && context.ById.TryGetValue(record.AppId, out var live))
        {
            var recordedMode = record.Mode.Length > 0 ? ParseMode(record.Mode) : mode;

            // Steam's fields differing from what was written can only mean somebody edited them,
            // even with our Target and key left in place: a mode switched by hand, a diagnostic
            // flag added. Restoring the recorded command would silently undo that, so it is left
            // alone. A route the user changes here is an update through the entry, not this.
            if (!OwnsRecorded(live, record))
            {
                return Entry(ImportAction.Conflict, EditedByHand, recordedMode, record.AppId, false);
            }

            // Untouched, but no longer what WSGM would write: the launcher moved with WSGM, or the
            // command format changed. Rewriting it is WSGM's own change to make.
            return Recompose(context.LauncherTarget, game.Key, recordedMode,
                       game.Multiplayer is MultiplayerVerdict.Multiplayer, record.Acknowledged) is { } fresh
                   && !CommandShortcut.Same(live, fresh.Target, fresh.LaunchOptions)
                ? Entry(ImportAction.Update, CommandChanged, recordedMode, record.AppId, true, true)
                : Entry(ImportAction.Skip, AlreadyImported, recordedMode, record.AppId, false);
        }

        // No live record, but Steam already has an entry launching this title. Adopting it is what
        // stops a second copy appearing every time the user re-runs a sync.
        var orphan = context.Unclaimed.FirstOrDefault(shortcut =>
            PackagedLauncherShortcut.Owns(shortcut, context.LauncherTarget, game.Key));
        if (orphan is not null)
        {
            // Adopted as what it currently launches, not as what the current default would write.
            // Taking over an entry must not quietly change how the game starts.
            var adopted = PackagedLauncherShortcut.TryReadMode(orphan.LaunchOptions, out var current)
                ? current
                : mode;
            return Entry(ImportAction.Adopt, AlreadyInSteam, adopted, orphan.AppId, true);
        }

        if (context.Claimed.Any(shortcut =>
                PackagedLauncherShortcut.Owns(shortcut, context.LauncherTarget, game.Key)))
        {
            return Entry(ImportAction.Add, ClaimedByAnotherTitle, mode, 0, true);
        }

        if (record is { ConfirmedUtc.Length: 0 })
        {
            // An earlier add that Steam did not confirm, and does not show now either. It may still
            // have succeeded, so adding again could make a second copy: offered, because only the
            // user can look at the library and say, but never ticked for them.
            return Entry(ImportAction.Add, NeverConfirmed, mode, 0, true) with { Unconfirmed = true };
        }

        return record is { AppId: > 0 }
            ? Entry(ImportAction.Add, DeletedFromSteam, mode, 0, true) with { ReplacedAppId = record.AppId }
            : Entry(ImportAction.Add, game.Launch.Evidence, mode, 0, true, true);
    }

    /// <summary>Describes a title that launches by a command rather than the packaged launcher.</summary>
    /// <remarks>
    ///     The same actions as a packaged title, with ownership by exact command. There is no input
    ///     mode and no acknowledgement: WSGM injects nothing into these, and Steam launches them as it
    ///     launches any non-Steam game. The route stands in for the mode.
    /// </remarks>
    private static ImportPlanEntry DescribeCommand(DiscoveredGame game, ImportedEntry? record, Context context)
    {
        var fallback = game.CommandRoutes[0].Id;

        ImportPlanEntry Command(ImportAction action, string reason, uint appId, bool selectable, string route,
            bool preselect = false)
        {
            return new ImportPlanEntry(game.SourceId, game.Key, game.Name, action, reason,
                ImportMode.SteamIntegration, false, false, appId, selectable, Route: route, Preselect: preselect);
        }

        if (record is { AppId: > 0 } && context.ById.TryGetValue(record.AppId, out var live))
        {
            var recordedRoute = record.Route.Length > 0 ? record.Route : fallback;
            if (!OwnsRecorded(live, record))
            {
                return Command(ImportAction.Conflict, EditedByHand, record.AppId, false, recordedRoute);
            }

            // A launcher title's executable on participating storage now starts through the content
            // check; its earlier direct shortcut moves to that route rather than to the launcher.
            if (recordedRoute == "direct" && game.CommandRoutes.All(route => route.Id != "direct")
                                          && game.CommandRoutes.FirstOrDefault(route => route.IsManaged) is { } tracked)
            {
                return Command(ImportAction.Update, TrackedStorage, record.AppId, true, tracked.Id, true);
            }

            // The route it was imported with may be gone: the launcher it went through was
            // uninstalled. The title still runs by its first route, but moving it there is the
            // user's call, so it is offered and not ticked.
            if (game.CommandRoutes.FirstOrDefault(route => route.Id == recordedRoute) is not { } offered)
            {
                return Command(ImportAction.Update,
                    "The way this title was imported is no longer available, so it would move to "
                    + $"{game.CommandRoutes[0].Label}.",
                    record.AppId, true, fallback);
            }

            // Untouched, but the source now describes a different command for the same route: the
            // install moved, or the launcher did.
            return CommandShortcut.TryCompose(offered, context.LauncherTarget, out var fresh, out _)
                   && !CommandShortcut.Same(live, fresh.Target, fresh.LaunchOptions)
                ? Command(ImportAction.Update, CommandChanged, record.AppId, true, recordedRoute, true)
                : Command(ImportAction.Skip, AlreadyImported, record.AppId, false, recordedRoute);
        }

        var matches = context.CommandMatches(game.CommandRoutes);
        foreach (var match in matches.Where(match => match.FolderAgrees && !context.IsClaimed(match.Shortcut.AppId)))
        {
            return Command(ImportAction.Adopt, AlreadyInSteam, match.Shortcut.AppId, true, match.Route.Id);
        }

        if (matches.Any(match => match.FolderAgrees && context.IsClaimed(match.Shortcut.AppId)))
        {
            return Command(ImportAction.Add, ClaimedByAnotherTitle, 0, true, fallback);
        }

        foreach (var match in matches.Where(match => !match.FolderAgrees && !context.IsClaimed(match.Shortcut.AppId)))
        {
            return Command(ImportAction.Conflict,
                "An existing shortcut has a different working directory and is left alone.", match.Shortcut.AppId,
                false, match.Route.Id);
        }

        if (record is { ConfirmedUtc.Length: 0 })
        {
            return Command(ImportAction.Add, NeverConfirmed, 0, true, fallback) with { Unconfirmed = true };
        }

        return record is { AppId: > 0 }
            ? Command(ImportAction.Add, DeletedFromSteam, 0, true, fallback) with { ReplacedAppId = record.AppId }
            : Command(ImportAction.Add, game.Launch.Evidence, 0, true, fallback, true);
    }

    /// <summary>What WSGM would write for a packaged title now, or null when it cannot say.</summary>
    private static ShortcutFields? Recompose(
        string launcherTarget, string key, ImportMode mode, bool multiplayer, bool acknowledged)
    {
        if (launcherTarget.Length == 0)
        {
            return null;
        }

        try
        {
            return PackagedLauncherShortcut.Compose(launcherTarget, key, mode, multiplayer, acknowledged);
        }
        catch (ArgumentException)
        {
            // A record the launcher would now refuse to compose is left as it is, not rewritten.
            return null;
        }
    }

    private static ImportPlanEntry Removal(ImportedEntry record, ImportAction action, string reason, bool selectable)
    {
        return new ImportPlanEntry(record.Source, record.Key, record.Name, action, reason,
            ParseMode(record.Mode), false, false, record.AppId, selectable, Route: record.Route);
    }

    private static ImportMode ParseMode(string mode)
    {
        return mode.Equals(nameof(ImportMode.SteamIntegration), StringComparison.OrdinalIgnoreCase)
            ? ImportMode.SteamIntegration
            : ImportMode.ControllerOnly;
    }

    /// <summary>What every title in one build is described against.</summary>
    private sealed class Context(
        IReadOnlyDictionary<uint, ExistingShortcut> byId,
        IReadOnlyList<ExistingShortcut> existing,
        HashSet<uint> claimed,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnroutable)
    {
        private readonly Dictionary<(string Program, string Arguments), List<ExistingShortcut>> _commands =
            IndexCommands(existing);

        private readonly
            Dictionary<(string Program, string Arguments, string Directory, string Marker), List<ExistingShortcut>>
            _follow = IndexFollow(existing);

        internal IReadOnlyDictionary<uint, ExistingShortcut> ById { get; } = byId;

        /// <summary>The shortcuts no record names, which are the only ones a title may adopt.</summary>
        internal IEnumerable<ExistingShortcut> Unclaimed =>
            existing.Where(shortcut => !claimed.Contains(shortcut.AppId));

        internal IEnumerable<ExistingShortcut> Claimed =>
            existing.Where(shortcut => claimed.Contains(shortcut.AppId));

        internal string LauncherTarget { get; } = launcherTarget;
        internal ImportMode DefaultMode { get; } = defaultMode;
        internal bool IncludeUnroutable { get; } = includeUnroutable;

        internal void Claim(uint appId)
        {
            claimed.Add(appId);
        }

        internal bool IsClaimed(uint appId)
        {
            return claimed.Contains(appId);
        }

        internal List<(ExistingShortcut Shortcut, ShortcutRoute Route, bool FolderAgrees)> CommandMatches(
            IReadOnlyList<ShortcutRoute> routes)
        {
            List<(ExistingShortcut, ShortcutRoute, bool)> result = [];
            foreach (var route in routes)
            {
                if (!CommandShortcut.TryCompose(route, LauncherTarget, out var fields, out _))
                {
                    continue;
                }

                var matches = !route.Follows || route.IsManaged
                    ? _commands.GetValueOrDefault((Program(fields.Target), fields.LaunchOptions.Trim()))
                    : _follow.GetValueOrDefault((Program(route.Target), route.LaunchOptions.Trim(),
                        Folder(route.FollowDirectory), Folder(route.FollowMarker)));
                if (matches is null)
                {
                    continue;
                }

                foreach (var shortcut in matches)
                {
                    if (route.Follows && !route.IsManaged &&
                        !CommandShortcut.SameProgram(shortcut.Target, fields.Target))
                    {
                        continue;
                    }

                    result.Add((shortcut, route,
                        CommandShortcut.SameFolder(shortcut.StartDirectory, fields.StartDirectory)));
                }
            }

            return result;
        }

        private static Dictionary<(string, string), List<ExistingShortcut>> IndexCommands(
            IReadOnlyList<ExistingShortcut> shortcuts)
        {
            Dictionary<(string, string), List<ExistingShortcut>> result = [];
            foreach (var shortcut in shortcuts)
            {
                var key = (Program(shortcut.Target), shortcut.LaunchOptions.Trim());
                if (!result.TryGetValue(key, out var matches))
                {
                    result[key] = matches = [];
                }

                matches.Add(shortcut);
            }

            return result;
        }

        private static Dictionary<(string, string, string, string), List<ExistingShortcut>> IndexFollow(
            IReadOnlyList<ExistingShortcut> shortcuts)
        {
            Dictionary<(string, string, string, string), List<ExistingShortcut>> result = [];
            foreach (var shortcut in shortcuts)
            {
                if (!PackagedLaunchCommand.TryParseFollow(shortcut.LaunchOptions.Trim(), out var request, out _))
                {
                    continue;
                }

                var key = (Program(request.Program), request.Arguments.Trim(), Folder(request.Directory),
                    Folder(request.Marker));
                if (!result.TryGetValue(key, out var matches))
                {
                    result[key] = matches = [];
                }

                matches.Add(shortcut);
            }

            return result;
        }

        private static string Program(string value)
        {
            return value.Trim().Trim('"').ToUpperInvariant();
        }

        private static string Folder(string value)
        {
            return Path.TrimEndingDirectorySeparator(value.Trim().Trim('"')).ToUpperInvariant();
        }
    }

    /// <summary>Both halves of an identity, compared the way Windows compares package names.</summary>
    private sealed class IdentityComparer : IEqualityComparer<(string Source, string Key)>
    {
        internal static readonly IdentityComparer Instance = new();

        public bool Equals((string Source, string Key) x, (string Source, string Key) y)
        {
            return string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Source, string Key) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
        }
    }
}
