using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>
///     The persisted Quick access pin list and the parts of the sheet that follow it directly: the
///     pin markers on section headings and source rows, the live mirrors of pinned action rows, the
///     placeholder tile and the pin toast.
/// </summary>
/// <remarks>
///     Pinned sections are drawn by the window from the Device, performance and graphics snapshots;
///     this owner handles only what follows the list itself. Pin ids are stored unchanged in
///     AppConfig.QuickAccessPins.
/// </remarks>
internal sealed class QuickAccessPins
{
    /// <summary>The Tag prefix that marks a Quick access clone; X on one unpins.</summary>
    internal const string TagPrefix = "pin:";

    // Every section heading currently in the window's tree; each adds itself on attach and leaves on detach.
    private readonly HashSet<SectionPinHeader> _headers = [];
    private readonly Panel _mirrorGrid;

    /// <summary>
    ///     Live mirrors on the Quick access root: each clone follows its source row's
    ///     title, description, badge and visibility through the source's property changes, and
    ///     presses through to the source's Click handlers.
    /// </summary>
    private readonly List<(ActionButton Source, EventHandler<AvaloniaPropertyChangedEventArgs> Handler)> _mirrors =
        [];

    /// <summary>
    ///     Every pinnable action row, by its stable id: the pages' tagged ActionButtons, and one card per
    ///     pinnable Tools section. Device rows are not here: they are rebuilt from the snapshot on every render.
    /// </summary>
    private readonly Dictionary<string, ActionButton> _rows = new(StringComparer.Ordinal);

    // The pinnable Tools sections, by pin id, with whether each is offered now.
    private readonly Dictionary<string, Func<bool>> _sections = new(StringComparer.Ordinal);

    private readonly Control _toast;
    private readonly TextBlock _toastHint;
    private readonly TextBlock _toastText;
    private bool _detached;
    private ActionButton? _ghost;

    /// <summary>False until the first Set, so restoring stored pins never toasts.</summary>
    private bool _initialized;

    // The pin list the current mirrors were built for. Mirrors follow their source rows through
    // property changes, so while the list is unchanged a render only revisits the pinned sections.
    private string[]? _mirrored;

    private DispatcherTimer? _toastTimer;

    /// <summary>Indexes the pinnable rows and takes over the mirror grid and the toast.</summary>
    /// <param name="root">The window whose tagged ActionButtons are the pinnable rows.</param>
    /// <param name="mirrorGrid">The Quick access grid that holds the row mirrors and the placeholder.</param>
    /// <param name="toast">The transient confirmation shown after a pin toggle.</param>
    /// <param name="toastText">The toast's message.</param>
    /// <param name="toastHint">The toast's secondary hint.</param>
    internal QuickAccessPins(Control root, Panel mirrorGrid, Control toast, TextBlock toastText, TextBlock toastHint)
    {
        _mirrorGrid = mirrorGrid;
        _toast = toast;
        _toastText = toastText;
        _toastHint = toastHint;
        foreach (var button in root.GetLogicalDescendants().OfType<ActionButton>())
        {
            if (button.Tag is string { Length: > 0 } id && !id.StartsWith(TagPrefix, StringComparison.Ordinal))
            {
                _rows[id] = button;
            }
        }
    }

    /// <summary>The pinned ids in display order.</summary>
    internal IReadOnlyList<string> Ids { get; private set; } = [];

    /// <summary>The pin id a stored pin is drawn and toggled under.</summary>
    /// <param name="id">A pin id read from AppConfig.QuickAccessPins.</param>
    /// <returns>
    ///     The id of the shared page's host-row group for a pin saved while Device drew WSGM-owned fallback
    ///     pages, which it did with integration off or before a device's layout arrived; any other id unchanged.
    /// </returns>
    internal static string CurrentId(string id)
    {
        return id switch
        {
            "section.device.power" => "section.device.plugin.power.configuration",
            "section.device.controller" or "section.device.controller-and-motion" =>
                "section.device.plugin.controller.configuration",
            "section.device.lighting" => "section.device.plugin.rgb.configuration",
            "section.device.diagnostics" => "section.device.plugin.info.configuration",
            _ => id
        };
    }

    /// <summary>Whether an id is pinned.</summary>
    /// <param name="id">A pin id.</param>
    /// <returns>True when the id is in the pin list.</returns>
    internal bool Contains(string id)
    {
        return Ids.Contains(id);
    }

    /// <summary>Resolves a pin id that names an action row or a Tools section, which pins as a tile.</summary>
    /// <param name="id">A pin id.</param>
    /// <returns>The pin, drawn as the row's mirror, or null for an id this source does not own.</returns>
    internal QuickAccessPin? Resolve(string id)
    {
        return _rows.TryGetValue(id, out var source)
            ? new QuickAccessPin(source.Title ?? id, source.IsVisible, true, _ => Mirror(id)!)
            : null;
    }

    /// <summary>Offers a Tools section as a pinnable card, under the pin id its registry entry declares.</summary>
    /// <param name="id">The pin id.</param>
    /// <param name="title">The card's title, the section's rail label.</param>
    /// <param name="description">The card's description.</param>
    /// <param name="icon">The section icon.</param>
    /// <param name="available">Whether the section is offered now; the card shows only then.</param>
    /// <param name="open">Opens the section, as choosing it on the rail does.</param>
    internal void AddSection(string id, string title, string description, Geometry? icon, Func<bool> available,
        Action open)
    {
        ActionButton source = new() { Title = title, Description = description, IconGeometry = icon };
        source.Click += (_, _) => open();
        _rows[id] = source;
        _sections[id] = available;
        source.IsVisible = available();
    }

    /// <summary>Shows each section card only while its section is offered.</summary>
    internal void RefreshSections()
    {
        foreach (var (id, available) in _sections)
        {
            _rows[id].IsVisible = available();
        }
    }

    /// <summary>
    ///     Takes the persisted pin list, refreshes the markers and confirms a change with the toast.
    ///     Ids this build cannot resolve stay in the list for the build or device that can.
    /// </summary>
    /// <param name="ids">The pinned row ids in display order.</param>
    internal void Set(IReadOnlyList<string> ids)
    {
        var previous = _initialized ? Ids : null;
        Ids = ids;
        _initialized = true;
        // The markers sit on the source rows, which show while Quick access is hidden.
        UpdateIndicators();
        if (previous is not null && ids.Count != previous.Count)
        {
            ShowToast(ids.Count > previous.Count);
        }
    }

    /// <summary>Lets a section heading follow the pin list while it is in the tree.</summary>
    /// <param name="header">A heading created for a section that can be pinned.</param>
    internal void Track(SectionPinHeader header)
    {
        header.Refresh(Contains(header.SectionId));
        // Pin indicator updates visit only the headers in the tree, not every control in the window.
        header.AttachedToLogicalTree += (_, _) =>
        {
            _headers.Add(header);
            header.Refresh(Contains(header.SectionId));
        };
        header.DetachedFromLogicalTree += (_, _) => _headers.Remove(header);
    }

    /// <summary>Rebuilds the row mirrors when the pin list changed since they were made.</summary>
    internal void SyncMirrors()
    {
        if (_mirrored is not null && _mirrored.SequenceEqual(Ids, StringComparer.Ordinal))
        {
            return;
        }

        ReleaseMirrors();
        _mirrorGrid.Children.Clear();
        foreach (var id in Ids)
        {
            if (_rows.TryGetValue(id, out var source))
            {
                var mirror = CreateMirror(id, source);
                mirror.Margin = new Thickness(0, 0, 10, 10);
                _mirrorGrid.Children.Add(mirror);
            }
        }

        _mirrored = [.. Ids];
    }

    /// <summary>The mirror of a pinned row, or null when the id is not a row.</summary>
    /// <param name="id">A pin id.</param>
    /// <returns>The mirror control in the grid.</returns>
    internal Control? Mirror(string id)
    {
        return _mirrorGrid.Children.FirstOrDefault(child => Equals(child.Tag, TagPrefix + id));
    }

    /// <summary>Shows the placeholder tile while no section is pinned and hides an empty grid.</summary>
    /// <param name="sectionsPinned">Whether any pinned section is drawn below the grid.</param>
    /// <returns>The number of row mirrors drawn, without the placeholder.</returns>
    internal int PlaceGhost(bool sectionsPinned)
    {
        // Once sections are present their headers explain pinning. Avoid an empty
        // action row above a front page containing only sections.
        if (!sectionsPinned)
        {
            _ghost ??= CreateGhost();
            if (!_mirrorGrid.Children.Contains(_ghost))
            {
                _mirrorGrid.Children.Add(_ghost);
            }
        }
        else if (_ghost is not null)
        {
            _mirrorGrid.Children.Remove(_ghost);
        }

        _mirrorGrid.IsVisible = _mirrorGrid.Children.Count > 0;
        return _mirrorGrid.Children.Count - (sectionsPinned ? 0 : 1);
    }

    /// <summary>Determines whether a tagged card is an original row in the active pin set.</summary>
    /// <param name="tag">Semantic tag attached to the focused control.</param>
    /// <param name="pinned">Current set of pinned group IDs.</param>
    /// <returns>True for an original source row whose group is pinned; pinned copies are excluded.</returns>
    internal static bool IsOriginalPinnedRow(object? tag, IReadOnlySet<string> pinned)
    {
        return tag is string id
               && !id.StartsWith(TagPrefix, StringComparison.Ordinal)
               && pinned.Contains(id);
    }

    /// <summary>Releases the mirrors' subscriptions and the toast as the window closes.</summary>
    internal void Detach()
    {
        _detached = true;
        _toastTimer?.Stop();
        _toastTimer = null;
        ReleaseMirrors();
    }

    /// <summary>Updates the pin marker on every row in its original destination.</summary>
    /// <remarks>
    ///     Section headers register themselves while they are in the tree, so replaced sections drop
    ///     out without a walk of the whole window. Quick access mirrors use a prefixed tag and remain
    ///     unmarked: the icon is the immediate feedback at the source row, where the user pressed X or
    ///     held the card.
    /// </remarks>
    private void UpdateIndicators()
    {
        var pinned = Ids.ToHashSet(StringComparer.Ordinal);
        foreach (var header in _headers)
        {
            header.Refresh(pinned.Contains(header.SectionId));
        }

        foreach (var (id, button) in _rows)
        {
            button.IsPinned = IsOriginalPinnedRow(id, pinned);
        }
    }

    /// <summary>Transient confirmation above the Open apps strip after a pin toggle.</summary>
    private void ShowToast(bool added)
    {
        if (_detached)
        {
            return;
        }

        _toastText.Text = added ? "Pinned to Quick access" : "Unpinned from Quick access";
        _toastHint.Text = added ? "X again to unpin" : string.Empty;
        _toast.IsVisible = true;
        if (_toastTimer is null)
        {
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(2400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _toast.IsVisible = false;
            };
            _toastTimer = timer;
        }

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static ActionButton CreateGhost()
    {
        ActionButton ghost = new()
        {
            IconGeometry = Icons.Pin,
            Title = "Pin a section",
            Description = "Use the tack button in a heading to show its controls here",
            IsEnabled = false,
            Margin = new Thickness(0, 0, 10, 10)
        };
        ghost.Classes.Add("tile");
        ghost.Classes.Add("ghost");
        return ghost;
    }

    private ActionButton CreateMirror(string id, ActionButton source)
    {
        var clone = new ActionButton { Tag = TagPrefix + id };
        clone.Classes.Add("tile");
        foreach (var cls in source.Classes)
        {
            if (cls is "primary" or "danger")
            {
                clone.Classes.Add(cls);
            }
        }

        Copy(clone, source);
        EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, _) => Copy(clone, source);
        source.PropertyChanged += handler;
        // Press-through: the source's Click handlers run with the source as sender, so a
        // row that rewrites its own title ("Really?", "Applied to …") does so on the source
        // and the mirror follows.
        clone.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _mirrors.Add((source, handler));
        return clone;
    }

    private void ReleaseMirrors()
    {
        foreach (var (source, handler) in _mirrors)
        {
            source.PropertyChanged -= handler;
        }

        _mirrors.Clear();
        _mirrored = null;
    }

    private static void Copy(ActionButton clone, ActionButton source)
    {
        clone.CopyContentFrom(source);
        // The source's OWN IsVisible (bound to a feature flag), not its effective one —
        // the source panel is hidden whenever the Quick access root shows.
        clone.IsVisible = source.IsVisible;
        clone.IsEnabled = source.IsEnabled;
    }
}
