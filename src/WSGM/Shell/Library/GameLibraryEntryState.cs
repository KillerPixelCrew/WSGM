using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One title in the Game Library review: its plan, what was discovered, and what the user chose.</summary>
/// <remarks>Every member is read and changed under the owning <see cref="GameLibraryService" />'s gate.</remarks>
/// <param name="id">The stable id derived from the title's source and key.</param>
/// <param name="plan">What the plan established about the title.</param>
/// <param name="game">What its source discovered, or a stand-in for a title no longer installed.</param>
internal sealed class GameLibraryEntryState(string id, ImportPlanEntry plan, DiscoveredGame game)
{
    /// <summary>The stable id, kept across scans.</summary>
    internal string Id { get; } = id;

    /// <summary>What the plan established about the title, as the user's edits and the last save left it.</summary>
    internal ImportPlanEntry Plan { get; set; } = plan;

    /// <summary>What its source discovered.</summary>
    internal DiscoveredGame Game { get; set; } = game;

    /// <summary>The title name the user gave a ROM, or empty.</summary>
    internal string TitleName { get; set; } = "";

    /// <summary>The title name the shortcut was last recorded with.</summary>
    internal string RecordedTitleName { get; set; } = "";

    /// <summary>Whether the user renamed the title since it was recorded.</summary>
    internal bool RenameRequested => TitleName.Length > 0 && TitleName != RecordedTitleName;

    /// <summary>The emulator installation the user picked for a ROM, or empty for the source default.</summary>
    internal string EmulatorOverride { get; set; } = "";

    /// <summary>The core the user picked for a ROM, or empty for the source default.</summary>
    internal string CoreOverride { get; set; } = "";

    /// <summary>Whether the managed content's launch changed since it was recorded.</summary>
    internal bool ContentChanged { get; set; }

    /// <summary>Whether the user selected the imported managed shortcut for cleanup.</summary>
    internal bool Cleanup { get; set; }

    /// <summary>The plan a cleanup replaced, restored when the cleanup is cancelled.</summary>
    internal ImportPlanEntry? BeforeCleanup { get; set; }

    /// <summary>The arguments the user typed for a ROM, or empty for the source default.</summary>
    internal List<string> ArgumentsOverride { get; set; } = [];

    /// <summary>The input mode a packaged title would launch with.</summary>
    internal ImportMode Mode { get; set; }

    /// <summary>The command route it would launch with, or empty for a packaged title.</summary>
    internal string Route { get; set; } = string.Empty;

    /// <summary>Whether the user accepted the multiplayer injection risk.</summary>
    internal bool Acknowledged { get; set; }

    /// <summary>Whether the user ticked it.</summary>
    internal bool Selected { get; set; }

    /// <summary>Whether the user said not to import this title.</summary>
    internal bool Excluded { get; set; }

    /// <summary>How many images were applied, or null before an import.</summary>
    internal int? ArtworkApplied { get; set; }

    /// <summary>The artwork the user picked, by type. A pick with no URL clears that type.</summary>
    internal Dictionary<ArtworkAsset, ArtworkPick> Picks { get; } = [];

    /// <summary>The picks already applied to this title's shortcut.</summary>
    internal Dictionary<ArtworkAsset, ArtworkPick> SettledPicks { get; } = [];

    /// <summary>The game the user matched the title to at the artwork providers, or null.</summary>
    internal ArtworkGameMatch? Match { get; set; }

    /// <summary>Whether it launches through the packaged launcher.</summary>
    /// <remarks>
    ///     The plan's route says so, not the discovery: a removal's stand-in has no routes whether
    ///     it was an Xbox title or a GOG one, and its record's route is what it was imported with.
    /// </remarks>
    internal bool Packaged => Plan.Route.Length == 0;

    /// <summary>Whether saving this entry writes a shortcut that runs WSGM.PackagedLaunch.</summary>
    internal bool NeedsLauncher =>
        Action is ImportAction.Add or ImportAction.Update && !ArtworkOnly
                                                          && (Packaged ||
                                                              Game.CommandRoutes.FirstOrDefault(candidate =>
                                                                      candidate.Id == Route) is { Follows: true } or
                                                                  { IsManaged: true });

    /// <summary>Whether it launches through the managed route, the only route that stores a content record.</summary>
    internal bool Managed => Game.Content is not null
                             && Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == Route) is
                                 { IsManaged: true };

    /// <summary>The entry's Steam app id, once it has one.</summary>
    internal uint AppId => Plan.AppId;

    /// <summary>Whether its launch and artwork can be changed here.</summary>
    internal bool Editable => Action is not (ImportAction.Remove or ImportAction.Conflict) && !Excluded;

    /// <summary>Whether the user has moved this entry off the mode or route Steam's entry launches with.</summary>
    private bool Rerouted => (Mode != Plan.Mode || Route != Plan.Route) && Plan.AppId > 0;

    /// <summary>Whether an entry Steam already has has artwork picked that is not applied yet.</summary>
    private bool ArtworkPending => Plan.AppId > 0 && Picks.Count > 0;

    /// <summary>Whether a choice the user made is waiting to be saved to an imported title.</summary>
    internal bool PendingChange => Plan.Action is ImportAction.Skip &&
                                   (Rerouted || ArtworkPending || ContentChanged || RenameRequested);

    /// <summary>Whether saving this entry means applying artwork and nothing else.</summary>
    internal bool ArtworkOnly => Plan.Action is ImportAction.Skip && !Rerouted && !ContentChanged &&
                                 !RenameRequested && ArtworkPending;

    /// <summary>What applying this entry would now do.</summary>
    /// <remarks>
    ///     An entry Steam already has is a Skip, or an Adopt, until the user changes its route or its
    ///     artwork; then it really does need saving. Without this the page would show the new mode
    ///     and never apply it, and an Adopt would record a mode its shortcut does not launch with,
    ///     because adopting writes nothing.
    /// </remarks>
    internal ImportAction Action =>
        Plan.Action switch
        {
            ImportAction.Skip when Rerouted || ArtworkPending || ContentChanged || RenameRequested =>
                ImportAction.Update,
            ImportAction.Adopt when Rerouted => ImportAction.Update,
            _ => Plan.Action
        };

    /// <summary>The action's published name: Artwork for an artwork-only save.</summary>
    internal string ActionName => ArtworkOnly ? "Artwork" : Action.ToString();

    /// <summary>Whether the user may tick this entry.</summary>
    internal bool Selectable => !Excluded && (Plan.Selectable || PendingChange);

    /// <summary>Whether "select all" may tick it: never a removal, an unconfirmed add, or a title the user deleted.</summary>
    internal bool BulkSelectable =>
        Selectable && Action is not ImportAction.Remove && !Plan.Unconfirmed && Plan.ReplacedAppId == 0;

    /// <summary>Why it cannot be ticked, when it cannot.</summary>
    internal string Reason => Excluded ? "You chose not to import this." : Plan.Reason;

    /// <summary>An entry's id: derived from its source and key, so a title keeps it across scans.</summary>
    /// <param name="source">The source the title came from.</param>
    /// <param name="key">The title's key within that source.</param>
    /// <returns>The stable id.</returns>
    internal static string IdOf(string source, string key)
    {
        return ManagedContentStorage.ContentId(source, key)[..24];
    }

    /// <summary>Why a mode cannot be used for an entry, or null when it can.</summary>
    /// <param name="plan">What the plan established about the title.</param>
    /// <param name="wanted">The mode asked for.</param>
    /// <param name="acknowledged">Whether the risk was accepted along with it.</param>
    /// <returns>The refusal in words the page shows, or null.</returns>
    /// <remarks>
    ///     One rule for both the command and a stored choice, so a choice saved under one set of
    ///     facts is judged again, not trusted, when the facts change.
    /// </remarks>
    internal static string? ModeRefusal(ImportPlanEntry plan, ImportMode wanted, bool acknowledged)
    {
        if (wanted is not ImportMode.SteamIntegration)
        {
            return null;
        }

        if (!plan.CanUseSteamIntegration)
        {
            return "This title has no validated launch route, so Steam integration is not available for it.";
        }

        return plan.RequiresAcknowledgement && !acknowledged
            ? "This title is marked multiplayer. Steam integration injects into it, so the risk has to be "
              + "accepted first."
            : null;
    }

    /// <summary>Whether the user may leave a title out.</summary>
    /// <param name="plan">The title's plan entry.</param>
    /// <returns>True for a title Steam does not have as ours yet.</returns>
    /// <remarks>
    ///     Only something not yet imported can be left out. An imported title is taken out of Steam
    ///     by removing it, which is a different and deliberate act.
    /// </remarks>
    internal static bool Excludable(ImportPlanEntry plan)
    {
        return plan.Action is ImportAction.Add or ImportAction.Adopt;
    }

    /// <summary>What the user decided about this entry, as it is kept across scans.</summary>
    /// <returns>A new choice record.</returns>
    internal ImportChoice ToChoice()
    {
        var picked = Packaged && Mode != Plan.Mode ? Mode : (ImportMode?)null;
        return new ImportChoice
        {
            Source = Plan.Source,
            Key = Plan.Key,
            Mode = picked?.ToString() ?? string.Empty,
            Acknowledged = picked is ImportMode.SteamIntegration && Acknowledged,
            Excluded = Excluded,
            Route = Route != Plan.Route ? Route : string.Empty,
            Artwork = [.. SettledPicks.Concat(Picks).GroupBy(pair => pair.Key)
                .Select(group => group.Last().Value)],
            MatchProvider = Match?.ProviderId ?? string.Empty,
            MatchId = Match?.Id ?? string.Empty,
            MatchName = Match?.Name ?? string.Empty,
            TitleName = TitleName,
            EmulatorInstallationId = EmulatorOverride,
            CoreId = CoreOverride,
            Cleanup = Cleanup,
            Arguments = [.. ArgumentsOverride]
        };
    }

    /// <summary>Takes the outcome of a save: Steam now has exactly what the entry says.</summary>
    /// <param name="appId">The confirmed shortcut id.</param>
    /// <param name="artworkApplied">How many images it has from WSGM.</param>
    /// <param name="settled">The account's artwork selections already handled for this shortcut.</param>
    /// <remarks>
    ///     Without this the entry kept its scan-time action: a title just imported showed as new,
    ///     and a later artwork pick went through an update that rewrote the shortcut.
    /// </remarks>
    internal void Settle(uint appId, int artworkApplied,
        IReadOnlyDictionary<ArtworkAsset, AppliedArtworkChoice> settled)
    {
        Plan = Plan with
        {
            Action = ImportAction.Skip,
            AppId = appId,
            Reason = "Imported.",
            Selectable = false,
            Preselect = false,
            Unconfirmed = false,
            ReplacedAppId = 0,
            Mode = Mode,
            Route = Packaged ? string.Empty : Route
        };
        ArtworkApplied = artworkApplied;
        foreach (var (asset, pick) in Picks.ToArray())
        {
            if (settled.TryGetValue(asset, out var applied) && applied.Url == pick.Url
                && applied.Revision == pick.Revision)
            {
                SettledPicks[asset] = pick;
                Picks.Remove(asset);
            }
        }
        Selected = Picks.Count > 0;
        ContentChanged = false;
        RecordedTitleName = Game.Content?.Name ?? Game.Name;
    }
}
