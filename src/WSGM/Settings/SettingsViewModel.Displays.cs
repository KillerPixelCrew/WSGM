using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    private readonly bool _queryDisplaysOnWorker;
    private bool _launchLoaded;
    private bool _displayDiscoveryClosed;
    private bool _readingDisplays;
    private bool _editingDesktopLayout;
    private DisplayArrangement? _observedDisplays;
    private string _displayDiscoveryText = "";
    private readonly List<DisplayTargetIdentity> _forgottenDisplays = [];

    /// <summary>Gets the read-only display discovery command.</summary>
    public AsyncRelayCommand RefreshDisplaysCommand { get; }
    /// <summary>Gets the command that copies today's arrangement into the selected draft.</summary>
    public AsyncRelayCommand CopyCurrentLayoutCommand { get; }
    /// <summary>Gets the command that selects the Game Mode draft.</summary>
    public RelayCommand EditGameLayoutCommand { get; }
    /// <summary>Gets the command that selects the Desktop draft.</summary>
    public RelayCommand EditDesktopLayoutCommand { get; }

    /// <summary>Gets or sets which saved layout the Display page edits.</summary>
    public bool EditingDesktopLayout
    {
        get => _editingDesktopLayout;
        set
        {
            if (_editingDesktopLayout == value) { return; }
            _editingDesktopLayout = value;
            foreach (string name in new[] { nameof(EditingDesktopLayout), nameof(CurrentDisplayLayout), nameof(ShowLayoutEditor), nameof(DisplayPolicySummary) })
            { Raise(name); }
        }
    }

    /// <summary>Gets the currently selected layout draft.</summary>
    public DisplayLayoutEditor CurrentDisplayLayout => EditingDesktopLayout ? DesktopLayout : GameLayout;
    /// <summary>Gets whether the current transition uses a saved layout.</summary>
    public bool ShowLayoutEditor => EditingDesktopLayout ? ShowDesktopLayout : ShowCustomLaunch;
    /// <summary>Gets the explanation for the selected transition's automatic policy.</summary>
    public string DisplayPolicySummary => EditingDesktopLayout
        ? "Restore the desktop arrangement WSGM saved when entering Game Mode."
        : "Keep the current display arrangement and use 100% scaling in Game Mode.";
    /// <summary>Gets whether display discovery is running.</summary>
    public bool ReadingDisplays => _readingDisplays;
    /// <summary>Gets whether discovery has a message to display.</summary>
    public bool HasDisplayDiscoveryMessage => DisplayDiscoveryText.Length > 0;
    /// <summary>Gets a discovery error or progress message without discarding saved displays.</summary>
    public string DisplayDiscoveryText
    {
        get => _displayDiscoveryText;
        private set { _displayDiscoveryText = value; Raise(nameof(DisplayDiscoveryText)); Raise(nameof(HasDisplayDiscoveryMessage)); }
    }

    internal void StartDisplayDiscovery()
    {
        if (_queryDisplaysOnWorker) { RefreshDisplaysCommand.Execute(null); }
    }

    internal void StopDisplayDiscovery() => _displayDiscoveryClosed = true;

    private void SeedDisplayLayout(DisplayLayoutEditor editor, bool game)
    {
        if (_observedDisplays is not { } observed) { return; }
        DisplayLayout layout = new([.. observed.Targets.Where(target => target.Active && target.Current is not null)
            .Select(target => game ? target.Current! with { DpiPercent = 100 } : target.Current!)]);
        if (layout.Outputs.Count > 0 && DisplayLayouts.Describe(layout) is null) { editor.CopyFrom(layout); }
    }

    private sealed record DisplayRead(DisplayArrangement Arrangement, IReadOnlyDictionary<string, DisplayCatalogFacts?> Facts);

    private DisplayRead ReadDisplayCatalog()
    {
        DisplayArrangement arrangement = _services.CaptureDisplays();
        Dictionary<string, DisplayCatalogFacts?> facts = [];
        foreach (DisplayTargetObservation display in arrangement.Targets.Where(target => target.Available))
        {
            try { facts[display.Target.DevicePath] = _services.ReadDisplayFacts(display.Target); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _services.Report("Could not read capabilities for " + display.Target.FriendlyName, ex); }
        }
        return new(arrangement, facts);
    }

    private Task RefreshDisplaysAsync() => ReadDisplaysAsync(copy: false);
    private Task CopyCurrentLayoutAsync() => ReadDisplaysAsync(copy: true);

    private async Task ReadDisplaysAsync(bool copy)
    {
        if (_readingDisplays || _displayDiscoveryClosed) { return; }
        _readingDisplays = true;
        Raise(nameof(ReadingDisplays));
        DisplayLayoutEditor destination = CurrentDisplayLayout;
        DisplayDiscoveryText = "Reading displays…";
        try
        {
            DisplayRead read = _queryDisplaysOnWorker ? await Task.Run(ReadDisplayCatalog) : ReadDisplayCatalog();
            if (_displayDiscoveryClosed) { return; }
            bool gameWasEmpty = !GameLayout.HasDisplays, desktopWasEmpty = !DesktopLayout.HasDisplays;
            _observedDisplays = read.Arrangement;
            // Preserve wait selection and every draft, including disabled outputs and invalid edits.
            _waitForDisplay = WaitForDisplayIndex > 0 && WaitForDisplayIndex <= KnownDisplays.Count
                ? KnownDisplays[WaitForDisplayIndex - 1].Target : null;
            MergeCatalog(read.Arrangement, read.Facts);
            GameLayout.RefreshCatalog(KnownDisplays, _present);
            DesktopLayout.RefreshCatalog(KnownDisplays, _present);
            RefreshDisplayChoices();
            if (copy)
            {
                DisplayLayout layout = new([.. read.Arrangement.Targets.Where(target => target.Active && target.Current is not null)
                    .Select(target => target.Current!)]);
                if (layout.Outputs.Count == 0 || DisplayLayouts.Describe(layout) is { })
                { DisplayDiscoveryText = "The current desktop could not be copied. Your draft has been kept."; return; }
                destination.CopyFrom(layout);
                StatusText = "Current desktop copied into the draft. Undo is available; save to keep it.";
            }
            else
            {
                if (gameWasEmpty && ShowCustomLaunch) { SeedDisplayLayout(GameLayout, game: true); }
                if (desktopWasEmpty && ShowDesktopLayout) { SeedDisplayLayout(DesktopLayout, game: false); }
            }
            DisplayDiscoveryText = _present.Count == 0
                ? "No connected displays could be read. Check the connection, then refresh. Saved displays are kept." : "";
            RefreshLaunchSummary();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_displayDiscoveryClosed) { return; }
            DisplayDiscoveryText = "Displays could not be read. Your drafts are kept. Refresh the display list to try again.";
            _services.Report("Reading displays for Settings failed", ex);
        }
        finally
        {
            _readingDisplays = false;
            if (!_displayDiscoveryClosed) { Raise(nameof(ReadingDisplays)); }
        }
    }
}
