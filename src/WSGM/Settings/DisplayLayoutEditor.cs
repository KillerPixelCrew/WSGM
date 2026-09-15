using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>What discovery learned about one display beyond its current placement.</summary>
/// <param name="Modes">Modes advertised by the driver or monitor EDID.</param>
/// <param name="HdrSupported">Whether the display reported advanced-colour support.</param>
/// <param name="MaximumDpiPercent">Highest scaling percentage it offered, or zero when unknown.</param>
internal sealed record DisplayCatalogFacts(
    IReadOnlyList<DisplayMode> Modes, bool HdrSupported, int MaximumDpiPercent);

/// <summary>One display in the layout editor, present or remembered.</summary>
public sealed class DisplayLayoutEditorRow : ObservableObject
{
    private bool _active;
    private bool _isPrimary;
    private int _x;
    private int _y;
    private DisplayMode? _mode;
    private int _dpiPercent = 100;
    private bool _hdrEnabled;
    private IReadOnlyList<DisplayResolution> _resolutions = [];
    private IReadOnlyList<int> _refreshRates = [];

    internal DisplayLayoutEditorRow(KnownDisplay display, bool present)
    {
        Display = display;
        Present = present;
        Modes = [.. display.Modes];
        _mode = Modes.FirstOrDefault();
        RefreshModeChoices();
    }

    /// <inheritdoc />
    /// <summary>Raised after any edit, so the owner can revalidate the whole layout.</summary>
    internal event Action? Edited;

    internal KnownDisplay Display { get; }

    /// <summary>The display's identity, or null for a catalog entry that lost it.</summary>
    internal DisplayTargetIdentity? Target => Display.Target;

    /// <summary>Gets the display's name.</summary>
    public string DisplayName => Display.Target?.FriendlyName is { Length: > 0 } name
        ? name
        : "Unnamed display";

    /// <summary>Gets whether the display is connected right now.</summary>
    public bool Present { get; private set; }

    /// <summary>Gets the badge shown for a display that is configured but not plugged in.</summary>
    public string PresenceText => Present ? "" : "Not connected right now";

    /// <summary>Gets whether this display's identity is one Windows can resolve.</summary>
    /// <remarks>
    /// False for a row migrated from the retired per-monitor profiles, which recorded a GDI name
    /// and a registry device key rather than a display identity. Such a row has to be pointed at a
    /// real display before Game Mode will apply the layout.
    /// </remarks>
    public bool NeedsRebind => Display.Target is not { } target
        || (target.DevicePath.Length == 0 && target.EdidManufacturerId is null);

    /// <summary>Gets the prompt shown for a row that still needs a display.</summary>
    public string RebindText => NeedsRebind
        ? "Confirm which display this is before Game Mode can apply the layout."
        : "";

    /// <summary>Gets whether advanced colour can be chosen for this display.</summary>
    public bool HdrSupported => Display.HdrSupported;

    /// <summary>Gets remembered driver modes and timings advertised by the monitor EDID.</summary>
    public IReadOnlyList<DisplayMode> Modes { get; private set; }

    internal void Refresh(bool present)
    {
        Present = present;
        Modes = [.. Display.Modes];
        _mode ??= Modes.FirstOrDefault();
        RaiseAll();
        foreach (string name in new[] { nameof(Present), nameof(PresenceText), nameof(Modes), nameof(HasModes), nameof(HdrSupported), nameof(HdrHint) })
        { Raise(name); }
    }

    /// <summary>Gets whether any mode is known, so the picker has something to offer.</summary>
    public bool HasModes => Modes.Count > 0 || Mode is not null;

    /// <summary>Gets the resolutions remembered for this display, including a saved mode.</summary>
    public IReadOnlyList<DisplayResolution> Resolutions => _resolutions;

    private IEnumerable<DisplayMode> AvailableModes => Mode is { } mode ? Modes.Append(mode) : Modes;

    /// <summary>Gets or sets the resolution while retaining a supported refresh rate.</summary>
    public DisplayResolution? Resolution
    {
        get => Mode is { } mode ? new(mode.Width, mode.Height) : null;
        set
        {
            if (value is not { } resolution || Resolution == resolution) { return; }
            var matches = AvailableModes.Where(mode => mode.Width == resolution.Width && mode.Height == resolution.Height).ToArray();
            Mode = matches.FirstOrDefault(mode => mode.RefreshHz == Mode?.RefreshHz) ?? matches.FirstOrDefault();
        }
    }

    /// <summary>Gets refresh rates belonging to the selected resolution.</summary>
    public IReadOnlyList<int> RefreshRates => _refreshRates;

    private void RefreshModeChoices()
    {
        DisplayResolution[] resolutions = [.. AvailableModes
            .Select(mode => new DisplayResolution(mode.Width, mode.Height)).Distinct()];
        int[] rates = [.. AvailableModes.Where(mode => mode.Width == Mode?.Width && mode.Height == Mode?.Height)
            .Select(mode => mode.RefreshHz).Distinct().OrderDescending()];
        // Replacing ItemsSource during a ComboBox selection resets its selection and can write
        // the first option back into the draft. Publish only an actual change in choices.
        if (!_resolutions.SequenceEqual(resolutions))
        {
            _resolutions = resolutions;
            Raise(nameof(Resolutions));
        }
        if (!_refreshRates.SequenceEqual(rates))
        {
            _refreshRates = rates;
            Raise(nameof(RefreshRates));
        }
    }

    /// <summary>Gets or sets a refresh rate from the selected resolution's supported modes.</summary>
    public int? RefreshHz
    {
        get => Mode?.RefreshHz;
        set
        {
            if (value is null || value == RefreshHz) { return; }
            Mode = AvailableModes.FirstOrDefault(mode => mode.Width == Mode?.Width && mode.Height == Mode?.Height
                && mode.RefreshHz == value) ?? Mode;
        }
    }

    /// <summary>Gets scaling choices within the remembered display range.</summary>
    public IReadOnlyList<int> Scales => [.. Enumerable.Range(4, Math.Max(1,
        Math.Min(500, Display.MaximumDpiPercent > 0 ? Display.MaximumDpiPercent : 500) / 25 - 3))
        .Select(step => step * 25).Append(DpiPercent).Distinct().Order()];

    /// <summary>Gets the numbered display label used by the diagram and list.</summary>
    public int Number { get; internal set; }

    /// <summary>Gets the state of this display in the draft.</summary>
    public string LayoutState => !Active ? "Off in this layout" : IsPrimary ? "Primary" : "Enabled";

    /// <summary>Gets the display connection state without hiding disconnected entries.</summary>
    public string ConnectionText => Present ? "Connected" : "Disconnected · Remembered display";

    /// <summary>Gets the heading of the selected-display inspector.</summary>
    public string InspectorTitle => $"{Number} · {DisplayName}";

    /// <summary>Gets the reason HDR is unavailable, when applicable.</summary>
    public string HdrHint => HdrSupported ? "" : "HDR is not supported by the remembered display capabilities.";

    /// <summary>Gets or sets whether this display is part of the layout.</summary>
    public bool Active
    {
        get => _active;
        set => Set(ref _active, value, nameof(Active));
    }

    /// <summary>Raised when this row is the one the user just made primary, so the editor demotes
    /// the others rather than guessing which of two primaries was meant.</summary>
    internal event Action<DisplayLayoutEditorRow>? PrimaryRequested;

    /// <summary>Gets or sets whether this display is the primary one.</summary>
    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            if (_isPrimary == value) { return; }
            _isPrimary = value;
            Raise(nameof(IsPrimary));
            Raise(nameof(LayoutState));
            if (value) { PrimaryRequested?.Invoke(this); }
            Edited?.Invoke();
        }
    }

    /// <summary>Gets or sets the desktop x position of the display's top-left corner.</summary>
    public int X
    {
        get => _x;
        set => Set(ref _x, value, nameof(X));
    }

    /// <summary>Gets or sets the desktop y position of the display's top-left corner.</summary>
    public int Y
    {
        get => _y;
        set => Set(ref _y, value, nameof(Y));
    }

    /// <summary>Gets or sets the chosen mode.</summary>
    public DisplayMode? Mode
    {
        get => _mode;
        set => Set(ref _mode, value, nameof(Mode));
    }

    /// <summary>Gets or sets the scaling percentage.</summary>
    public int DpiPercent
    {
        get => _dpiPercent;
        set => Set(ref _dpiPercent, value, nameof(DpiPercent));
    }

    /// <summary>Gets or sets whether advanced colour is requested.</summary>
    public bool HdrEnabled
    {
        get => _hdrEnabled;
        set => Set(ref _hdrEnabled, value, nameof(HdrEnabled));
    }

    /// <summary>Gets or sets the HDR choice as Off or On.</summary>
    public int HdrIndex { get => HdrEnabled ? 1 : 0; set => HdrEnabled = value == 1; }

    /// <summary>Fills the row from a saved output.</summary>
    internal void Load(DisplayLayoutOutput output)
    {
        _active = true;
        _isPrimary = output.IsPrimary;
        _x = output.X;
        _y = output.Y;
        _mode = Modes.FirstOrDefault(mode =>
            mode.Width == output.Width && mode.Height == output.Height
            && Math.Abs(mode.RefreshHz - output.Refresh.Hertz) < 1)
            ?? new DisplayMode(output.Width, output.Height, (int)Math.Round(output.Refresh.Hertz));
        _dpiPercent = output.DpiPercent ?? 100;
        _hdrEnabled = output.Hdr ?? false;
        RaiseAll();
    }

    /// <summary>Builds the saved output this row describes, or null when it is switched off or has
    /// nothing usable to say.</summary>
    internal DisplayLayoutOutput? ToOutput() =>
        Active && Target is { } target && Mode is { Width: > 0, Height: > 0 } mode
            ? new(target, X, Y, mode.Width, mode.Height, DisplayRefresh.FromHertz(mode.RefreshHz),
                DpiPercent: DpiPercent,
                Hdr: HdrSupported ? HdrEnabled : null)
            : null;

    /// <summary>Points this row at a real display, keeping the values the user already chose.</summary>
    internal void Rebind(DisplayTargetIdentity target)
    {
        Display.Target = target;
        RaiseAll();
        Edited?.Invoke();
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) { return; }
        field = value;
        Raise(name);
        if (name == nameof(HdrEnabled)) { Raise(nameof(HdrIndex)); }
        if (name == nameof(Mode))
        {
            RefreshModeChoices();
            foreach (string related in new[] { nameof(Resolution), nameof(RefreshHz), nameof(HasModes) })
            { Raise(related); }
        }
        Raise(nameof(LayoutState));
        Edited?.Invoke();
    }

    private void RaiseAll()
    {
        RefreshModeChoices();
        foreach (string name in new[]
        {
            nameof(Active), nameof(IsPrimary), nameof(X), nameof(Y), nameof(Mode),
            nameof(DpiPercent), nameof(HdrEnabled), nameof(DisplayName), nameof(NeedsRebind),
            nameof(RebindText),
            nameof(Resolution), nameof(RefreshHz), nameof(Scales),
            nameof(LayoutState), nameof(InspectorTitle), nameof(ConnectionText), nameof(HdrIndex), nameof(HasModes),
        })
        {
            Raise(name);
        }
    }
}

/// <summary>Edits one saved layout, one row per remembered display.
///
/// Every remembered display gets a row whether or not it is plugged in, because the reference
/// machine's television is invisible to Windows until an HDMI switch selects this PC and the layout
/// still has to be authored. A row is a request, not an observation.
///
/// The rules are <see cref="DisplayLayouts.Describe"/>, the same ones the apply enforces, so an
/// editor cannot save something Windows would refuse. Choosing a primary display normalizes the
/// whole arrangement so that display sits at 0,0, which is where Windows puts it; asking a user to
/// do that subtraction themselves would be a way to fail the rule for no reason.</summary>
public sealed class DisplayLayoutEditor : ObservableObject
{
    private readonly Action _changed;
    private DisplayLayoutEditorRow? _requestedPrimary;
    private bool _loading;
    private string _validationText = "";
    private DisplayLayoutEditorRow? _selected;
    private readonly Stack<RowState[]> _undo = new();
    private RowState[] _previous = [];

    private sealed record RowState(DisplayLayoutEditorRow Row, bool Active, bool Primary,
        int X, int Y, DisplayMode? Mode, int Scale, bool Hdr);

    internal DisplayLayoutEditor(Action changed) => _changed = changed;

    /// <inheritdoc />
    /// <summary>Gets one row per remembered display.</summary>
    public ObservableCollection<DisplayLayoutEditorRow> Rows { get; } = [];

    /// <summary>Gets or sets the display whose settings are being edited.</summary>
    public DisplayLayoutEditorRow? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) { return; }
            _selected = value;
            Raise(nameof(Selected));
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedNeedsRebind));
        }
    }

    /// <summary>Gets whether the inspector has a display.</summary>
    public bool HasSelection => Selected is not null;
    /// <summary>Gets whether the selected saved identity needs confirmation.</summary>
    public bool SelectedNeedsRebind => Selected?.NeedsRebind is true;
    /// <summary>Gets whether discovery or saved state supplied any display.</summary>
    public bool HasDisplays => Rows.Count > 0;
    /// <summary>Gets whether the layout has an enabled output.</summary>
    public bool HasActiveDisplays => Rows.Any(row => row.Active);
    /// <summary>Gets whether this draft has an undo step.</summary>
    public bool CanUndo => _undo.Count > 0;
    /// <summary>Gets whether an enabled display is currently disconnected.</summary>
    public bool HasDisconnectedDisplay => Rows.Any(row => row.Active && !row.Present);

    /// <summary>Gets why this layout cannot be saved, or an empty string when it can.</summary>
    public string ValidationText
    {
        get => _validationText;
        private set
        {
            if (_validationText == value) { return; }
            _validationText = value;
            Raise(nameof(ValidationText));
            Raise(nameof(HasValidationError));
        }
    }

    /// <summary>Gets whether the layout is currently unusable.</summary>
    public bool HasValidationError => ValidationText.Length > 0;

    /// <summary>Gets whether any row still needs a display chosen for it.</summary>
    public bool HasUnboundRow => Rows.Any(row => row.Active && row.NeedsRebind);

    /// <summary>Rebuilds the rows from the catalog and a saved layout.</summary>
    /// <param name="catalog">Every remembered display.</param>
    /// <param name="present">Displays connected right now.</param>
    /// <param name="layout">The saved layout, or null for a layout that has not been made yet.</param>
    internal void Load(
        IReadOnlyList<KnownDisplay> catalog,
        IReadOnlyList<DisplayTargetIdentity> present,
        DisplayLayout? layout)
    {
        DisplayTargetIdentity? selected = Selected?.Target;
        _loading = true;
        try
        {
            foreach (DisplayLayoutEditorRow existing in Rows) { Detach(existing); }
            Rows.Clear();
            foreach (KnownDisplay display in catalog)
            {
                DisplayLayoutEditorRow row = new(display,
                    display.Target is { } target && present.Any(other => other.Matches(target)));
                if (layout?.Outputs.FirstOrDefault(output => Same(output, display)) is { } output)
                {
                    row.Load(output);
                }
                Attach(row);
            }
            // A layout whose displays are not in the catalog at all would otherwise disappear on
            // load and be silently replaced by an empty one on the next save.
            foreach (DisplayLayoutOutput orphan in layout?.Outputs.Where(
                output => !catalog.Any(display => Same(output, display))) ?? [])
            {
                KnownDisplay adopted = new() { Target = orphan.Target };
                DisplayLayoutEditorRow row = new(adopted, false);
                row.Load(orphan);
                Attach(row);
            }
        }
        finally { _loading = false; }
        for (int i = 0; i < Rows.Count; i++) { Rows[i].Number = i + 1; }
        Selected = Rows.FirstOrDefault(row => selected is not null && row.Target?.Matches(selected) == true)
            ?? Rows.FirstOrDefault(row => row.IsPrimary) ?? Rows.FirstOrDefault();
        _undo.Clear();
        Revalidate();
        _previous = CaptureRows();
        Raise(nameof(HasDisplays));
        Raise(nameof(CanUndo));
    }

    /// <summary>Builds the layout the rows describe, or null when none is active.</summary>
    internal DisplayLayout? Build()
    {
        List<DisplayLayoutOutput> outputs = [.. Rows.Select(row => row.ToOutput()).OfType<DisplayLayoutOutput>()];
        return outputs.Count == 0 ? null : new(Normalize(outputs));
    }

    /// <summary>Removes a display from the editor and from the catalog it came from.</summary>
    /// <param name="row">The row to forget.</param>
    internal void Forget(DisplayLayoutEditorRow row)
    {
        Detach(row);
        Rows.Remove(row);
        if (Selected == row) { Selected = Rows.FirstOrDefault(); }
        for (int i = 0; i < Rows.Count; i++) { Rows[i].Number = i + 1; }
        _undo.Clear();
        Revalidate();
        _previous = CaptureRows();
        Raise(nameof(HasDisplays));
        Raise(nameof(CanUndo));
    }

    private void Attach(DisplayLayoutEditorRow row)
    {
        row.Edited += OnRowEdited;
        row.PrimaryRequested += OnPrimaryRequested;
        Rows.Add(row);
    }

    private void Detach(DisplayLayoutEditorRow row)
    {
        row.Edited -= OnRowEdited;
        row.PrimaryRequested -= OnPrimaryRequested;
        if (_requestedPrimary == row) { _requestedPrimary = null; }
    }

    private void OnPrimaryRequested(DisplayLayoutEditorRow row) => _requestedPrimary = row;

    /// <summary>Moves the primary display to the origin and everything else with it.</summary>
    private static IReadOnlyList<DisplayLayoutOutput> Normalize(IReadOnlyList<DisplayLayoutOutput> outputs)
    {
        DisplayLayoutOutput? primary = outputs.FirstOrDefault(output => output.IsPrimary);
        if (primary is not null || outputs.Count == 0) { return outputs; }
        // Nobody sits at the origin. Shift the arrangement so the top-left display does, which is
        // the only choice that keeps every relative position the user arranged.
        int offsetX = outputs.Min(output => output.X);
        int offsetY = outputs.Min(output => output.Y);
        return [.. outputs.Select(output => output with { X = output.X - offsetX, Y = output.Y - offsetY })];
    }

    private static bool Same(DisplayLayoutOutput output, KnownDisplay display) =>
        display.Target is { } target && output.Target.Matches(target);

    private void OnRowEdited()
    {
        if (_loading) { return; }
        _undo.Push(_previous);
        Revalidate();
        _previous = CaptureRows();
        Raise(nameof(CanUndo));
        _changed();
    }

    private RowState[] CaptureRows() => [.. Rows.Select(row => new RowState(row, row.Active, row.IsPrimary,
        row.X, row.Y, row.Mode, row.DpiPercent, row.HdrEnabled))];

    /// <summary>Refreshes discovery facts without replacing rows or losing draft edits.</summary>
    internal void RefreshCatalog(IReadOnlyList<KnownDisplay> catalog, IReadOnlyList<DisplayTargetIdentity> present)
    {
        _loading = true;
        try
        {
            foreach (KnownDisplay display in catalog)
            {
                bool connected = display.Target is { } target && present.Any(other => other.Matches(target));
                DisplayLayoutEditorRow? existing = Rows.FirstOrDefault(row => row.Display == display
                    || (display.Target is { } identity && row.Target?.Matches(identity) == true));
                if (existing is not null) { existing.Refresh(connected); }
                else { Attach(new(display, connected) { Number = Rows.Count + 1 }); }
            }
        }
        finally { _loading = false; }
        Selected ??= Rows.FirstOrDefault();
        Revalidate();
        _previous = CaptureRows();
        Raise(nameof(HasDisplays));
    }

    /// <summary>Undoes one completed edit to this layout.</summary>
    public void Undo()
    {
        if (!_undo.TryPop(out RowState[]? previous)) { return; }
        _loading = true;
        try
        {
            foreach (RowState state in previous)
            {
                state.Row.Active = state.Active;
                state.Row.IsPrimary = state.Primary;
                state.Row.X = state.X;
                state.Row.Y = state.Y;
                state.Row.Mode = state.Mode;
                state.Row.DpiPercent = state.Scale;
                state.Row.HdrEnabled = state.Hdr;
            }
        }
        finally { _loading = false; }
        _requestedPrimary = null;
        Revalidate();
        _previous = CaptureRows();
        Raise(nameof(CanUndo));
        _changed();
    }

    /// <summary>Copies observed values into the draft as one undoable edit.</summary>
    internal void CopyFrom(DisplayLayout layout)
    {
        _loading = true;
        try
        {
            foreach (DisplayLayoutEditorRow row in Rows)
            {
                if (layout.Outputs.FirstOrDefault(output => row.Target?.Matches(output.Target) == true) is { } output)
                { row.Load(output); }
                else { row.Active = false; row.IsPrimary = false; }
            }
        }
        finally { _loading = false; }
        OnRowEdited();
    }

    /// <summary>Moves a display, preserving the primary origin and recording a single undo step.</summary>
    public void Move(DisplayLayoutEditorRow row, int x, int y)
    {
        if (!Rows.Contains(row) || (row.X == x && row.Y == y)) { return; }
        _loading = true;
        try { row.X = Math.Clamp(x, -32768, 32768); row.Y = Math.Clamp(y, -32768, 32768); }
        finally { _loading = false; }
        OnRowEdited();
    }

    /// <summary>Places a display beside the primary without requiring pixel arithmetic.</summary>
    public void PlaceSelected(int position)
    {
        if (Selected is not { IsPrimary: false, Mode: { } mode } row
            || Rows.FirstOrDefault(item => item.Active && item.IsPrimary)?.Mode is not { } primary) { return; }
        (int x, int y) = position switch
        {
            1 => (primary.Width, 0),
            2 => (-mode.Width, 0),
            3 => (0, -mode.Height),
            4 => (0, primary.Height),
            _ => (row.X, row.Y),
        };
        Move(row, x, y);
    }

    /// <summary>Applies the one rule the editor enforces itself, then asks the library for the
    /// rest. Exactly one primary is a choice the user makes by clicking, so it is corrected here
    /// rather than reported.</summary>
    private void Revalidate()
    {
        DisplayLayoutEditorRow[] active = [.. Rows.Where(row => row.Active)];
        if (active.Length > 0 && active.Count(row => row.IsPrimary) != 1)
        {
            _loading = true;
            try
            {
                // The row the user just clicked wins. Taking the first primary in list order would
                // silently undo the click whenever another display already held the flag.
                DisplayLayoutEditorRow chosen =
                    _requestedPrimary is { } requested && active.Contains(requested) ? requested
                    : active.FirstOrDefault(row => row.IsPrimary) ?? active[0];
                foreach (DisplayLayoutEditorRow row in Rows) { row.IsPrimary = row == chosen; }
            }
            finally { _loading = false; }
        }
        if (active.FirstOrDefault(row => row.IsPrimary) is { } primary)
        {
            _loading = true;
            try
            {
                int offsetX = primary.X, offsetY = primary.Y;
                foreach (DisplayLayoutEditorRow row in active)
                {
                    row.X -= offsetX;
                    row.Y -= offsetY;
                }
            }
            finally { _loading = false; }
        }

        Raise(nameof(HasUnboundRow));
        Raise(nameof(SelectedNeedsRebind));
        Raise(nameof(HasActiveDisplays));
        Raise(nameof(HasDisconnectedDisplay));
        Raise(nameof(Rows));
        if (active.Length == 0)
        {
            ValidationText = "";
            return;
        }
        if (active.Any(row => row.Mode is null))
        {
            ValidationText = "Choose a resolution for every enabled display. Connect an unknown display and refresh the display list.";
            return;
        }
        if (HasUnboundRow)
        {
            ValidationText = "One or more displays still need to be identified.";
            return;
        }
        DisplayLayout? built = Build();
        ValidationText = built is null ? "" : DisplayLayouts.Describe(built) ?? "";
    }

    /// <summary>Renders a mode the way a user reads it.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>Resolution and refresh rate.</returns>
    public static string Describe(DisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return string.Create(CultureInfo.InvariantCulture,
            $"{mode.Width}x{mode.Height} @ {mode.RefreshHz} Hz");
    }
}
