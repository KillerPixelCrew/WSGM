using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The Game Library in the overlay: the same pipeline the Steam page drives, one level at a time.</summary>
/// <remarks>
///     <para>
///         The overlay's surface of the Game Library. It renders the service's published state and
///         calls the same methods the Steam page does, so a change made here is what the page shows
///         next and the other way round.
///     </para>
///     <para>
///         The service outlives this view: a scan or an apply keeps running when the overlay closes,
///         and opening the view again lands on its current state, including an apply in progress.
///         Leaving the view never cancels anything.
///     </para>
/// </remarks>
public sealed partial class GameLibraryView : ServiceSubView
{
    private IGameLibraryOverlaySource? _service;

    /// <inheritdoc />
    protected override string LogScope => "Game Library";

    /// <summary>Raised when the user asks to continue in Steam, such as to change a title's artwork.</summary>
    /// <remarks>The overlay controller carries this out: it opens the page, closes the sheet and focuses Steam.</remarks>
    internal event Action<uint, string>? ArtworkRequested;

    /// <summary>Attaches the view to the session's library, or detaches it with null.</summary>
    /// <param name="service">The library, or null when the overlay closes or the session has none.</param>
    internal void Attach(IGameLibraryOverlaySource? service)
    {
        _service = service;
        AttachSource(service);
    }

    private protected override void RenderHome()
    {
        var stack = NewStack("Game Library");
        if (_service?.ReadState() is not { } state)
        {
            stack.Children.Add(Caption("The Game Library is not available in this session."));
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(state.Reading.Count > 0
            ? $"Brings games from {string.Join(", ", state.Reading)} into Steam."
            : "No source is ticked and installed."));
        AddStatus(stack, state);

        if (state.Loading)
        {
            stack.Children.Add(Tagged(Row("Stop", "Stops after the title in progress; nothing is rolled back",
                Icons.Close, () => Run(_service.CancelAsync)), "stop"));
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(GameLibraryRows.Summary(state)));
        stack.Children.Add(Tagged(Row($"Sources ({state.Reading.Count} on)",
            "Tick the launchers and folders a scan reads", Icons.ListLines,
            () => Navigate(RenderSources)), "sources"));
        stack.Children.Add(Tagged(Row("Scan for games", "Look again. Nothing is written until you apply",
            Icons.Restart, () => Run(_service.ScanAsync)), "scan"));
        if (state.Entries.Count > 0)
        {
            stack.Children.Add(Tagged(Row($"Review ({state.Entries.Count})",
                "Choose what to import, how each launches, and change artwork", Icons.ListLines,
                () => Navigate(() => RenderReview(false))), "review"));
        }

        var imported = state.Entries.Count(GameLibraryRows.InSteam);
        if (imported > 0)
        {
            stack.Children.Add(Tagged(Row($"Imported games ({imported})",
                "Titles already in Steam: change their launch mode or artwork", Icons.Grid4,
                () => Navigate(() => RenderReview(true))), "imported"));
        }

        if (state.SelectedCount > 0)
        {
            stack.Children.Add(Tagged(PrimaryRow($"Apply {state.SelectedCount}",
                state.LauncherAvailable ? "Write the selected entries to Steam" : state.LauncherDetail ?? string.Empty,
                Icons.Play, () => Run(_service.ApplyAsync)), "apply"));
        }

        SetContent(stack);
    }

    private void RenderSources()
    {
        var stack = NewStack("Sources");
        if (_service?.ReadState() is not { } state)
        {
            SetContent(stack);
            return;
        }

        AddStatus(stack, state);
        var busy = state.Loading;
        foreach (var source in state.Sources)
        {
            var id = source.Id;
            var enabled = source.Enabled;
            stack.Children.Add(Tagged(Row(source.Name, GameLibraryRows.SourceLine(source),
                source.Installed ? null : Icons.BlockedCircle,
                source.Installed && !busy
                    ? () => Run(token => _service.SetSourceEnabledAsync(id, !enabled, token))
                    : null), "source:" + id));
        }

        var folders = state.Sources.Where(source => source.Kind == GameLibrarySourceKinds.Folder).ToList();
        if (folders.Count > 0)
        {
            stack.Children.Add(SectionLabel("REMOVE A FOLDER"));
            foreach (var folder in folders)
            {
                var id = folder.Id;
                stack.Children.Add(Tagged(Row($"Remove {folder.Name}",
                    "Stops reading it. Its imported titles stay in Steam until you remove them", Icons.Close,
                    busy ? null : () => Run(token => _service.RemoveFolderAsync(id, token))), "remove:" + id));
            }
        }

        stack.Children.Add(Tagged(
            Row("Add a shortcuts folder", "Choose a folder, recursion and file types", Icons.Grid4,
                () => Navigate(RenderAddFolder)), "add-folder"));
        var collections = state.CreateCollections;
        stack.Children.Add(Tagged(Row(collections ? "Steam collections: on" : "Steam collections: off",
            "One collection per launcher and folder, holding its imported games", Icons.ListLines,
            () => Run(token => _service.SetCollectionsAsync(!collections, token))), "collections"));
        SetContent(stack);
    }

    private void RenderReview(bool importedOnly)
    {
        var stack = NewStack(importedOnly ? "Imported games" : "Review");
        if (_service?.ReadState() is not { } state)
        {
            SetContent(stack);
            return;
        }

        RenderReviewInto(stack, state, importedOnly);
        SetContent(stack);
    }

    private void RenderEntry(string id)
    {
        if (_service?.ReadState() is not { } state || state.Entries.FirstOrDefault(entry => entry.Id == id) is not
                { } entry)
        {
            // A rescan replaced the list this entry belonged to.
            RenderMessage("Review", "That title is no longer listed. Go back and pick it again.");
            return;
        }

        var stack = NewStack(entry.Name);
        AddStatus(stack, state);
        stack.Children.Add(Caption(entry.Reason));

        if (entry.Selectable)
        {
            stack.Children.Add(Tagged(Row(entry.Selected ? "Deselect" : "Select",
                entry.Selected ? "Leave it out of the next apply" : "Include it in the next apply",
                Icons.ListLines, () => Run(token => _service.ToggleEntryAsync(id, token))), "toggle"));
        }

        var canCycle = entry.Editable
                       && (entry.Packaged ? entry.CanUseSteamIntegration : entry.Routes.Count > 1);
        stack.Children.Add(Tagged(Row(
            $"{(entry.Packaged ? "Launch mode" : "Launch route")}: {entry.LaunchLabel}",
            GameLibraryRows.LaunchChoice(entry), Icons.Rocket,
            canCycle ? () => CycleLaunch(entry.Id, entry.Name) : null), "launch"));

        if (entry.Editable)
        {
            stack.Children.Add(Tagged(Row("Staged artwork", GameLibraryRows.Artwork(entry), Icons.Palette,
                () => Navigate(() => RenderTitleArtwork(id, "grid"))), "artwork"));
            if (entry.AppId > 0)
            {
                stack.Children.Add(Tagged(Row("Change current Steam artwork",
                    "Applies immediately to the imported game", Icons.Palette,
                    () => ArtworkRequested?.Invoke(entry.AppId, entry.Name)), "artwork.current"));
            }
        }

        if (entry.Excluded)
        {
            stack.Children.Add(Tagged(Row("Offer again", "List it for import again",
                Icons.Restart, () => Run(token => _service.IncludeAsync(id, token))), "include"));
        }
        else if (entry.Action is nameof(ImportAction.Add) or nameof(ImportAction.Adopt))
        {
            stack.Children.Add(Tagged(Row("Don't import", "Leave it out of this and every later scan",
                Icons.BlockedCircle, () => Run(token => _service.ExcludeAsync(id, token))), "exclude"));
        }

        if (entry.Editable && !state.Loading)
        {
            if (!entry.Packaged && entry.Routes.Count > 0)
            {
                stack.Children.Add(ChoiceRow("Launch route",
                    entry.Routes.Select(route => (route.Id, route.Label)).ToArray(), entry.Route,
                    route => Run(token => _service!.SetRouteAsync(id, route, token))));
            }

            if (entry.Packaged)
            {
                stack.Children.Add(ChoiceRow("Input mode",
                    new[]
                    {
                        (nameof(ImportMode.ControllerOnly), "Controller only"),
                        (nameof(ImportMode.SteamIntegration), "Steam integration")
                    }, entry.Mode,
                    mode => ChooseMode(entry, mode)));
            }
        }

        stack.Children.Add(SectionLabel("DETAILS"));
        if (_service.ReadDetails(id) is { } details)
        {
            stack.Children.Add(Caption($"{entry.LaunchLabel}: {details.LaunchEvidence}"));
            stack.Children.Add(Caption($"Multiplayer: {details.Multiplayer}. {details.MultiplayerEvidence}"));
            stack.Children.Add(Caption(details.Identity));
            foreach (var note in details.Notes)
            {
                stack.Children.Add(Caption(note));
            }
        }

        stack.Children.Add(Caption(entry.MatchName.Length > 0
            ? $"Artwork matched to {entry.MatchName}."
            : "Artwork not matched to a game yet."));
        SetContent(stack);
    }

    /// <summary>Moves a title to its next launch mode or route; the service says when the risk has to be accepted first.</summary>
    private void CycleLaunch(string id, string name)
    {
        _ = RunSafelyAsync(CycleLaunchCoreAsync(id, name), "launch");
    }

    private async Task CycleLaunchCoreAsync(string id, string name)
    {
        var result = await _service!.CycleLaunchAsync(id, CancellationToken.None);
        if (!result.Command.Succeeded)
        {
            Toast(result.Command.Error ?? "That did not work.");
            return;
        }

        if (result.NeedsAcknowledgement)
        {
            Navigate(() => RenderAcknowledge(id, name));
        }
    }

    // Its own level, as on the Steam page, rather than a row: the user is accepting a risk to their
    // account, and that should not be one press away in a list they are scrolling through.
    private void RenderAcknowledge(string id, string name)
    {
        var stack = NewStack("Accept the risk?");
        stack.Children.Add(Caption($"{name} is marked as a multiplayer title. The Steam overlay route loads "
                                   + "Steam's overlay into the running game. Anti-cheat compatibility has not "
                                   + "been established for any title, and some anti-cheat systems treat that "
                                   + "as tampering."));
        stack.Children.Add(Caption("Controller only injects nothing and is the safer choice for a multiplayer game."));
        stack.Children.Add(Tagged(Row("Keep controller only", "Go back without changing anything",
            Icons.ArrowLeft, () => Back()), "keep"));
        stack.Children.Add(Tagged(DangerRow("Accept the risk and use the Steam overlay",
            "I understand this may risk a ban on this title", Icons.Rocket, () =>
            {
                Run(token => _service!.SetModeAsync(id, nameof(ImportMode.SteamIntegration), true, token));
                Back();
            }), "accept"));
        SetContent(stack);
    }

    private void AddStatus(StackPanel stack, GameLibraryState state)
    {
        if (state.Phase == GameLibraryPhases.Scanning)
        {
            stack.Children.Add(Caption("Scanning…"));
        }
        else if (state.Phase == GameLibraryPhases.Applying)
        {
            stack.Children.Add(Caption($"Applying {state.Progress} of {state.ProgressTotal}…"));
        }

        if (state.Error is { Length: > 0 } error)
        {
            stack.Children.Add(Caption(error));
        }
        else if (state.Notice is { Length: > 0 } notice)
        {
            stack.Children.Add(Caption(notice));
        }
    }
}
