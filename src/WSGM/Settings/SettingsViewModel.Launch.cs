using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    private DisplayLayout? _desktopLayout;
    private List<PluginActionStep> _desktopStartupActions = [];
    private List<PluginActionStep> _desktopWakeActions = [];
    private List<PluginActionStep> _enterActions = [];

    private DisplayLayout? _gameLayout;
    private List<PluginActionStep> _leaveActions = [];
    private IReadOnlyList<DisplayTargetIdentity> _present = [];
    private DisplayTargetIdentity? _waitForDisplay;

    /// <summary>Gets the command that removes a display from the remembered catalog.</summary>
    public RelayCommand<DisplayLayoutEditorRow> ForgetDisplayCommand { get; }

    /// <summary>Gets the command that appends the selected action to one list.</summary>
    public RelayCommand<PluginActionListEditor> AddActionStepCommand { get; }

    /// <summary>Gets the command that removes one action step.</summary>
    public RelayCommand<PluginActionStepEditorRow> RemoveActionStepCommand { get; }

    /// <summary>Gets the command that runs one step earlier.</summary>
    public RelayCommand<PluginActionStepEditorRow> MoveActionStepUpCommand { get; }

    /// <summary>Gets the command that runs one step later.</summary>
    public RelayCommand<PluginActionStepEditorRow> MoveActionStepDownCommand { get; }

    /// <summary>Edits the Game Mode layout.</summary>
    public DisplayLayoutEditor GameLayout { get; }

    /// <summary>Edits the Desktop layout leaving Game Mode restores.</summary>
    public DisplayLayoutEditor DesktopLayout { get; }

    /// <summary>Edits saved audio preferences applied when entering Game Mode.</summary>
    public AudioProfileEditor GameAudioProfile { get; }

    /// <summary>Edits saved audio preferences applied when returning to Desktop.</summary>
    public AudioProfileEditor DesktopAudioProfile { get; }

    /// <summary>The four action lists, in the order the page shows them.</summary>
    public IReadOnlyList<PluginActionListEditor> ActionLists { get; }

    /// <summary>Displays that can be chosen in the layout editor, present or remembered.</summary>
    public ObservableCollection<KnownDisplay> KnownDisplays { get; } = [];

    /// <summary>Selected <see cref="GameModeLaunchKind" /> index.</summary>
    public int GameModeLaunchKindIndex
    {
        get;
        set
        {
            field = value;
            Raise(nameof(GameModeLaunchKindIndex));
            Raise(nameof(ShowCustomLaunch));
            Raise(nameof(ShowLayoutEditor));
            if (_launchLoaded && ShowCustomLaunch && GameLayout is { HasActiveDisplays: false, CanUndo: false })
            {
                SeedDisplayLayout(GameLayout, true);
            }

            if (_launchLoaded)
            {
                RefreshLaunchSummary();
            }
        }
    }

    /// <summary>Whether the layout and wait fields apply to the selected launch kind.</summary>
    public bool ShowCustomLaunch => GameModeLaunchKindIndex == (int)GameModeLaunchKind.Custom;

    /// <summary>Selected <see cref="GameModeReturn" /> index.</summary>
    public int GameModeReturnIndex
    {
        get;
        set
        {
            field = value;
            Raise(nameof(GameModeReturnIndex));
            Raise(nameof(ShowDesktopLayout));
            Raise(nameof(ShowLayoutEditor));
            if (_launchLoaded && ShowDesktopLayout && DesktopLayout is { HasActiveDisplays: false, CanUndo: false })
            {
                SeedDisplayLayout(DesktopLayout, false);
            }

            if (_launchLoaded)
            {
                RefreshLaunchSummary();
            }
        }
    }

    /// <summary>Whether a Desktop layout is configured rather than captured at entry.</summary>
    public bool ShowDesktopLayout => GameModeReturnIndex == (int)GameModeReturn.DesktopLayout;

    /// <summary>What the saved layouts describe, including any validation problem.</summary>
    public string LaunchSummaryText
    {
        get;
        private set => SetField(ref field, value, nameof(LaunchSummaryText));
    } = "";

    /// <summary>Index into <see cref="WaitForDisplayChoices" />; zero means no wait.</summary>
    public int WaitForDisplayIndex
    {
        get;
        set => SetField(ref field, value, nameof(WaitForDisplayIndex));
    }

    /// <summary>"No display wait" followed by one entry per remembered display.</summary>
    public ObservableCollection<string> WaitForDisplayChoices { get; } = [];

    /// <summary>Whether both layouts currently describe a desktop Windows would accept.</summary>
    public bool CanSaveLayouts =>
        (!ShowCustomLaunch || GameLayout is { HasActiveDisplays: true, HasValidationError: false })
        && (!ShowDesktopLayout || DesktopLayout is { HasActiveDisplays: true, HasValidationError: false })
        && !ActionLists.Any(list => list.HasValidationError);

    /// <summary>Seeds the launch fields from a stored configuration.</summary>
    /// <param name="launch">The stored configuration.</param>
    private void LoadLaunchConfiguration(GameModeLaunchConfiguration launch)
    {
        GameModeLaunchKindIndex = (int)launch.Kind;
        GameModeReturnIndex = (int)launch.Return;
        _gameLayout = launch.GameLayout;
        _desktopLayout = launch.DesktopLayout;
        GameAudioProfile.Load(launch.GameAudio);
        DesktopAudioProfile.Load(launch.DesktopAudio);
        _waitForDisplay = launch.WaitForDisplay;
        _enterActions = launch.EnterActions;
        _leaveActions = launch.LeaveActions;
        _desktopStartupActions = launch.DesktopStartupActions;
        _desktopWakeActions = launch.DesktopWakeActions;

        KnownDisplays.Clear();
        foreach (var display in launch.KnownDisplays)
        {
            KnownDisplays.Add(display);
        }

        // Injected readers provide the initial fixture observation here. Production discovery
        // starts on a worker after the Settings window opens.
        if (!_queryDisplaysOnWorker)
        {
            try
            {
                _observedDisplays = _services.CaptureDisplays();
                MergeCatalog(_observedDisplays);
            }
            catch (Exception ex)
            {
                DisplayDiscoveryText = "Displays could not be read. Refresh the display list to try again.";
                _services.Report("Could not read the current displays for Settings", ex);
            }
        }

        RefreshLaunchRows();
        _launchLoaded = true;
    }

    /// <summary>Rebuilds every rendered row from the fields the editor owns.</summary>
    private void RefreshLaunchRows()
    {
        GameLayout.Load(KnownDisplays, _present, _gameLayout);
        DesktopLayout.Load(KnownDisplays, _present, _desktopLayout);

        var options = ReadPluginActions();
        ActionLists[0].Load(_enterActions, options);
        ActionLists[1].Load(_leaveActions, options);
        ActionLists[2].Load(_desktopStartupActions, options);
        ActionLists[3].Load(_desktopWakeActions, options);

        RefreshDisplayChoices();
    }

    private void RefreshDisplayChoices()
    {
        WaitForDisplayChoices.Clear();
        WaitForDisplayChoices.Add("No display wait");
        foreach (var display in KnownDisplays)
        {
            WaitForDisplayChoices.Add(display.Target?.FriendlyName ?? "Unnamed display");
        }

        WaitForDisplayIndex = _waitForDisplay is null
            ? 0
            : Math.Max(0,
                KnownDisplays.ToList().FindIndex(display => display.Target?.Matches(_waitForDisplay) == true) + 1);

        RefreshLaunchSummary();
    }

    /// <summary>
    ///     Restates what the two layouts describe. Called on every edit, so the page says
    ///     whether the layout can be saved while it is being changed rather than only on Save.
    /// </summary>
    private void RefreshLaunchSummary()
    {
        var active = GameLayout.Rows.Count(row => row.Active);
        LaunchSummaryText = GameModeLaunchKindIndex == (int)GameModeLaunchKind.Default
            ? "Game Mode starts on the display Windows calls primary and adjusts scaling only."
            : active == 0
                ? "Enable at least one display for Game Mode."
                : GameLayout.ValidationText.Length > 0
                    ? "Game Mode layout: " + GameLayout.ValidationText
                    : ShowDesktopLayout && DesktopLayout.ValidationText.Length > 0
                        ? "Desktop layout: " + DesktopLayout.ValidationText
                        : $"{active} display(s) in the Game Mode layout.";
        Raise(nameof(CanSaveLayouts));
    }

    private void ForgetDisplay(DisplayLayoutEditorRow? row)
    {
        if (row is null)
        {
            return;
        }

        foreach (var editor in new[] { GameLayout, DesktopLayout })
        {
            foreach (var other in editor.Rows.Where(candidate => candidate.Display == row.Display).ToList())
            {
                editor.Forget(other);
            }
        }

        KnownDisplays.Remove(row.Display);
        if (row.Target is { } forgotten)
        {
            _forgottenDisplays.Add(forgotten);
        }

        if (_waitForDisplay is { } wait && row.Target?.Matches(wait) == true)
        {
            _waitForDisplay = null;
        }

        RefreshDisplayChoices();
        RefreshLaunchSummary();
    }

    private IReadOnlyList<PluginActionOption> ReadPluginActions()
    {
        try
        {
            return _services.ReadPluginActions();
        }
        catch (Exception ex)
        {
            _services.Report("Could not read the running plugin actions for Settings", ex);
            return [];
        }
    }

    private void RemoveActionStep(PluginActionStepEditorRow? row)
    {
        if (row is null)
        {
            return;
        }

        foreach (var list in ActionLists.Where(list => list.Rows.Contains(row)))
        {
            list.Remove(row);
        }
    }

    private void MoveActionStep(PluginActionStepEditorRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        foreach (var list in ActionLists.Where(list => list.Rows.Contains(row)))
        {
            list.Move(row, delta);
        }
    }

    /// <summary>
    ///     Adds what this observation knows about each display to the remembered catalog, so a
    ///     display stays configurable after it is unplugged.
    /// </summary>
    private void MergeCatalog(DisplayArrangement arrangement,
        IReadOnlyDictionary<string, DisplayCatalogFacts?>? factsByDisplay = null)
    {
        _present = [.. arrangement.Targets.Where(target => target.Available).Select(target => target.Target)];
        foreach (var observed in arrangement.Targets)
        {
            if (!observed.Available)
            {
                continue;
            }

            var existing = KnownDisplays.FirstOrDefault(display => display.Target?.Matches(observed.Target) == true);
            if (existing is null)
            {
                existing = new KnownDisplay { Target = observed.Target };
                KnownDisplays.Add(existing);
            }

            existing.Target = observed.Target;
            existing.LastSeen = arrangement.CapturedAt;
            // Disabled sources still expose monitor EDID. Retain the broader driver-mode list
            // remembered while active rather than replacing it with descriptor-only timings.
            if ((factsByDisplay is null
                    ? _services.ReadDisplayFacts(observed.Target)
                    : factsByDisplay.GetValueOrDefault(observed.Target.DevicePath)) is { } facts)
            {
                if (facts.Modes.Count > 0)
                {
                    existing.Modes = observed.Active
                        ? [.. facts.Modes]
                        : [.. existing.Modes.Concat(facts.Modes).Distinct()];
                }

                existing.HdrSupported |= facts.HdrSupported;
                existing.MaximumDpiPercent = Math.Max(existing.MaximumDpiPercent, facts.MaximumDpiPercent);
            }

            if (observed.Current?.Hdr is not null)
            {
                existing.HdrSupported = true;
            }

            if (observed.Current is not { } current)
            {
                continue;
            }

            // The mode it is running is worth keeping even when enumeration failed, so a
            // remembered display always offers at least what it was last seen doing.
            DisplayMode running = new(current.Width, current.Height,
                (int)Math.Round(current.Refresh.Hertz));
            if (running is { Width: > 0, Height: > 0 } && !existing.Modes.Contains(running))
            {
                existing.Modes.Add(running);
            }
        }
    }

    /// <summary>
    ///     Captures the launch fields and remembered displays from the editor. Persistence
    ///     merges concurrent discoveries while honoring explicit Forget actions.
    /// </summary>
    /// <param name="launch">The section to write into.</param>
    private void ApplyLaunchTo(GameModeLaunchConfiguration launch)
    {
        launch.Kind = (GameModeLaunchKind)Math.Clamp(GameModeLaunchKindIndex, 0, 1);
        launch.Return = (GameModeReturn)Math.Clamp(GameModeReturnIndex, 0, 1);
        launch.GameLayout = GameLayout.Build();
        launch.DesktopLayout = DesktopLayout.Build();
        launch.GameAudio = GameAudioProfile.Build();
        launch.DesktopAudio = DesktopAudioProfile.Build();
        launch.WaitForDisplay = WaitForDisplayIndex > 0 && WaitForDisplayIndex <= KnownDisplays.Count
            ? KnownDisplays[WaitForDisplayIndex - 1].Target
            : null;
        launch.EnterActions = ActionLists[0].Build();
        launch.LeaveActions = ActionLists[1].Build();
        launch.DesktopStartupActions = ActionLists[2].Build();
        launch.DesktopWakeActions = ActionLists[3].Build();
        launch.KnownDisplays = [.. KnownDisplays];
    }

    /// <summary>One action a running plugin instance offers, for the action lists.</summary>
    /// <param name="Identity">The plugin instance.</param>
    /// <param name="Action">The declared action.</param>
    /// <param name="Label">How to name it in a picker.</param>
    public sealed record PluginActionOption(
        PluginInstanceIdentity Identity,
        PluginAction Action,
        string Label)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Label;
        }
    }
}
