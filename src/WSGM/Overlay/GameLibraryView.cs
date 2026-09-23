using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
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
public sealed class GameLibraryView : OverlaySubView
{
    private GameLibraryService? _service;

    /// <summary>Raised when the user asks to continue in Steam, such as to change a title's artwork.</summary>
    /// <remarks>The overlay controller carries this out: it opens the page, closes the sheet and focuses Steam.</remarks>
    internal event Action<GameLibrarySteamTarget>? OpenInSteamRequested;

    /// <inheritdoc />
    protected override string LogScope => "Game Library";

    /// <summary>Attaches the view to the session's library, or detaches it with null.</summary>
    /// <param name="service">The library, or null when the overlay closes or the session has none.</param>
    internal void Attach(GameLibraryService? service)
    {
        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
        }

        _service = service;
        if (service is not null)
        {
            service.Changed += OnServiceChanged;
        }
    }

    /// <summary>Opens the view on its home level.</summary>
    public void Open()
    {
        _stack.Clear();
        _current = null;
        _navigationGeneration++;
        Navigate(RenderHome);
    }

    /// <inheritdoc />
    private protected override void SetContent(StackPanel stack)
    {
        // The service republishes on every change, including the user's own toggles. Rebuilding the
        // level would otherwise throw focus back to the top of a list the user is working down.
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var tag = focused?.Tag as string;
        base.SetContent(stack);
        if (tag is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var match = stack.GetLogicalDescendants().OfType<Control>()
                .FirstOrDefault(control => Equals(control.Tag, tag) && control.Focusable);
            match?.Focus(NavigationMethod.Directional);
        });
    }

    private void OnServiceChanged()
    {
        // Raised from the service's own work, on whatever thread finished it.
        Dispatcher.UIThread.Post(() =>
        {
            if (_service is not null && IsVisible)
            {
                _current?.Invoke();
            }
        });
    }

    private void RenderHome()
    {
        var stack = NewStack("Game Library");
        if (_service?.ReadState() is not { } state)
        {
            stack.Children.Add(Caption("The Game Library is not available in this session."));
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(state.Sources.Count > 0
            ? $"Brings games from {string.Join(", ", state.Sources)} into Steam."
            : "No sources are registered."));
        AddStatus(stack, state);

        if (state.Phase is "scanning" or "applying")
        {
            stack.Children.Add(Tagged(Row("Stop", "Stops after the title in progress; nothing is rolled back",
                Icons.Close, () => Run(_service.CancelAsync)), "stop"));
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(GameLibraryRows.Summary(state)));
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

        stack.Children.Add(Tagged(Row("Open in Steam", "Continue on the Game Library page in Big Picture",
            Icons.SteamLike, () => OpenInSteamRequested?.Invoke(GameLibrarySteamTarget.Library)), "open-in-steam"));
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

        AddStatus(stack, state);
        if (!importedOnly && state.Entries.Any(entry => entry.Selectable))
        {
            var selected = state.SelectedCount > 0;
            stack.Children.Add(Tagged(Row(selected ? "Clear selection" : "Select all",
                selected ? $"{state.SelectedCount} selected" : "Everything that can be imported",
                Icons.ListLines, () => Run(token => _service.SelectAllAsync(!selected, token))), "select-all"));
        }

        if (!importedOnly && state.SelectedCount > 0 && state.Phase is not ("scanning" or "applying"))
        {
            stack.Children.Add(Tagged(PrimaryRow($"Apply {state.SelectedCount}", "Write the selected entries to Steam",
                Icons.Play, () => Run(_service.ApplyAsync)), "apply"));
        }

        foreach (var entry in state.Entries.Where(entry => !importedOnly || GameLibraryRows.InSteam(entry)))
        {
            var id = entry.Id;
            stack.Children.Add(Tagged(Row(entry.Name, GameLibraryRows.Describe(entry), null,
                () => Navigate(() => RenderEntry(id))), "entry:" + id));
        }

        SetContent(stack);
    }

    private void RenderEntry(string id)
    {
        if (_service?.ReadState() is not { } state || state.Entries.FirstOrDefault(entry => entry.Id == id) is not { } entry)
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

        stack.Children.Add(Tagged(Row($"Launch mode: {GameLibraryRows.Mode(entry.Mode)}",
            GameLibraryRows.ModeChoice(entry), Icons.Rocket,
            entry.CanUseSteamIntegration ? () => ChangeMode(entry) : null), "mode"));

        if (entry.AppId > 0 && entry.Action != "Remove")
        {
            stack.Children.Add(Tagged(Row("Change artwork…", "Opens this title's artwork page in Steam",
                Icons.Palette, () => OpenInSteamRequested?.Invoke(new GameLibrarySteamTarget(entry.AppId, entry.Name))),
                "artwork"));
        }

        if (entry.Excluded)
        {
            stack.Children.Add(Tagged(Row("Offer again", "List it for import again",
                Icons.Restart, () => Run(token => _service.IncludeAsync(id, token))), "include"));
        }
        else if (entry.Action is "Add" or "Adopt")
        {
            stack.Children.Add(Tagged(Row("Don't import", "Leave it out of this and every later scan",
                Icons.BlockedCircle, () => Run(token => _service.ExcludeAsync(id, token))), "exclude"));
        }

        stack.Children.Add(SectionLabel("DETAILS"));
        stack.Children.Add(Caption($"{entry.LaunchLabel}: {entry.LaunchEvidence}"));
        stack.Children.Add(Caption($"Multiplayer: {entry.Multiplayer}. {entry.MultiplayerEvidence}"));
        stack.Children.Add(Caption(GameLibraryRows.Artwork(entry)));
        stack.Children.Add(Caption($"From {entry.Source}: {entry.Identity}"));
        foreach (var note in entry.Notes)
        {
            stack.Children.Add(Caption(note));
        }

        SetContent(stack);
    }

    private void ChangeMode(GameLibraryEntry entry)
    {
        if (entry.Mode == nameof(ImportMode.SteamIntegration))
        {
            Run(token => _service!.SetModeAsync(entry.Id, nameof(ImportMode.ControllerOnly), false, token));
            return;
        }

        if (entry.RequiresAcknowledgement)
        {
            Navigate(() => RenderAcknowledge(entry.Id, entry.Name));
            return;
        }

        Run(token => _service!.SetModeAsync(entry.Id, nameof(ImportMode.SteamIntegration), false, token));
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
        if (state.Phase == "scanning")
        {
            stack.Children.Add(Caption("Scanning…"));
        }
        else if (state.Phase == "applying")
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

    private void Run(Func<CancellationToken, Task<SteamUiCommandResult>> command)
    {
        _ = RunSafelyAsync(RunCoreAsync(command), "command");
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task<SteamUiCommandResult>> command)
    {
        var result = await command(CancellationToken.None);
        if (!result.Succeeded)
        {
            Toast(result.Error ?? "That did not work.");
        }
    }

    private static ActionButton Tagged(ActionButton button, string tag)
    {
        button.Tag = tag;
        return button;
    }
}
