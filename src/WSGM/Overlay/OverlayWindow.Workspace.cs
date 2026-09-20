using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly Dictionary<string, FAInfoBadge> _sectionBadges = [];
    private readonly List<WorkspaceSection> _workspaceSections = [];
    private bool _selectingSection;
    private Dictionary<OverlayDestination, string> SelectedSections => _session.Sections;

    private Button? SelectedSectionButton => SectionRail.Children.OfType<Button>()
        .FirstOrDefault(button => button.Classes.Contains("selected"));

    private void RefreshWorkspace()
    {
        WorkspaceTitle.Text = DestinationLabel(_navigation.Destination);
        WorkspaceDescription.Text = _navigation.Destination switch
        {
            OverlayDestination.QuickAccess => "Your pinned controls and widgets",
            OverlayDestination.Device => "Power, performance and your device",
            OverlayDestination.Steam => "Library and game launch settings",
            OverlayDestination.System => "Display, storage and system tools",
            _ => "Wake, idle and session controls"
        };
        if (_navigation.Destination == OverlayDestination.Device)
        {
            RefreshDeviceSectionRail(_deviceBridge?.Snapshot()
                                     ?? new DeviceOverlaySnapshot(false, "Device integration off", string.Empty, null,
                                         []),
                _performanceSource?.Snapshot());
            return;
        }

        var sections = new List<WorkspaceSection>();
        if (_navigation.Destination == OverlayDestination.QuickAccess)
        {
            sections.Add(new WorkspaceSection("quick-access", "Pinned controls", Icons.Pin,
                OverlayPage.QuickAccess, null, null));
        }
        else
        {
            foreach (var action in ((Panel)DestinationPanel()).Children.OfType<ActionButton>()
                     .Where(action => action.IsVisible && action.CommandParameter is OverlayPage))
            {
                var page = (OverlayPage)action.CommandParameter!;
                sections.Add(new WorkspaceSection(page.ToString(), action.Title ?? string.Empty,
                    action.IconGeometry, page, null, null));
            }
        }

        ReconcileSectionRail(sections);
    }

    private void RefreshDeviceSectionRail(DeviceOverlaySnapshot snapshot, PerformanceOverlaySnapshot? performance)
    {
        if (_navigation.Destination != OverlayDestination.Device)
        {
            return;
        }

        var entries = new List<WorkspaceSection>
        {
            new("device.overview", "Overview", Icons.Gear, OverlayPage.Device, null, null)
        };
        var sections = DeviceOverlaySectionPages.Build(snapshot, performance);
        entries.AddRange(sections.Select(entry =>
            new WorkspaceSection(DeviceOverlaySectionPages.FocusKey(entry), entry.Title,
                SectionIconFor(entry.Icon) ?? Icons.ListLines, entry.Page, entry.PluginSectionId,
                entry.PluginSectionId is null ? entry.Section : null)));
        ReconcileSectionRail(entries);
        foreach (var entry in sections)
        {
            if (_sectionBadges.TryGetValue(DeviceOverlaySectionPages.FocusKey(entry), out var badge))
            {
                badge.Value = entry.Count;
                badge.IsVisible = entry.Count > 0;
            }
        }
    }

    private void ReconcileSectionRail(IReadOnlyList<WorkspaceSection> entries)
    {
        var focusedKey = (FocusManager?.GetFocusedElement() as Control)?.Tag as string;
        // Values never participate in this comparison. Telemetry must not replace rail focus targets.
        if (!_workspaceSections.SequenceEqual(entries))
        {
            _workspaceSections.Clear();
            _workspaceSections.AddRange(entries);
            SectionRail.Children.Clear();
            _sectionBadges.Clear();
            foreach (var entry in entries)
            {
                var button = CreateSectionButton(entry);
                SectionRail.Children.Add(button);
                if (Equals(button.Tag, focusedKey))
                {
                    button.Focus(NavigationMethod.Directional);
                }
            }
        }

        var selectedKey = SelectedSections.GetValueOrDefault(_navigation.Destination);
        if (!_selectingSection && _navigation.Destination == OverlayDestination.Device
                               && selectedKey is not null && entries.All(entry => entry.Key != selectedKey)
                               && entries.FirstOrDefault() is { } fallback)
        {
            // The descriptor owner retracted the open page. Retire its text-entry callback
            // before replacing the route, then land on the surviving overview rail.
            CloseAllSurfaces();
            SelectWorkspaceSection(fallback, true);
            return;
        }

        SyncSectionSelection();
        if (focusedKey?.StartsWith("rail.", StringComparison.Ordinal) == true
            && entries.All(entry => "rail." + entry.Key != focusedKey))
        {
            SelectedSectionButton?.Focus(NavigationMethod.Directional);
        }
    }

    private Button CreateSectionButton(WorkspaceSection entry)
    {
        var face = new Grid { ColumnDefinitions = new ColumnDefinitions("4,24,*,Auto"), ColumnSpacing = 12 };
        face.Children.Add(new Border
            { Classes = { "selection-rule" }, Height = 24, VerticalAlignment = VerticalAlignment.Center });
        var icon = new Path
        {
            Data = entry.Icon, Width = 20, Stretch = Stretch.Uniform,
            StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("DeckSecondaryBrush"));
        Grid.SetColumn(icon, 1);
        face.Children.Add(icon);
        var label = new TextBlock
        {
            Text = entry.Title, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 2);
        face.Children.Add(label);
        var badge = new FAInfoBadge { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(badge, 3);
        face.Children.Add(badge);
        _sectionBadges.Add(entry.Key, badge);
        var button = new Button { Content = face, Tag = "rail." + entry.Key, Classes = { "deck-section" } };
        AutomationProperties.SetName(button, entry.Title);
        ToolTip.SetTip(button, entry.Title);
        button.Click += (_, _) => SelectWorkspaceSection(entry, true);
        return button;
    }

    private void SelectWorkspaceSection(WorkspaceSection section, bool focusRail)
    {
        if (_selectingSection || HasActiveSurface)
        {
            return;
        }

        _selectingSection = true;
        try
        {
            LeaveAllNestedPages();
            _navigation.Select(_navigation.Destination);
            SelectedSections[_navigation.Destination] = section.Key;
            if (section.PluginSection is { } plugin)
            {
                EnterDevicePluginSection(plugin);
            }
            else if (section.DeviceSection is { } device)
            {
                EnterDeviceSection(device);
            }
            else if (section.Page == OverlayPage.Device)
            {
                PanelDevice.IsVisible = true;
                RefreshDevicePanel();
                RefreshPerformancePanel();
            }
            else if (section.Page != OverlayPage.QuickAccess)
            {
                EnterSubView(section.Page);
            }

            ContentScroller.Offset = default;
            SyncSectionSelection();
            if (focusRail)
            {
                SelectedSectionButton?.Focus(NavigationMethod.Directional);
            }
        }
        finally
        {
            _selectingSection = false;
        }
    }

    private void SelectRememberedSection(bool focusRail)
    {
        var remembered = SelectedSections.GetValueOrDefault(_navigation.Destination);
        var section = _workspaceSections.FirstOrDefault(item => item.Key == remembered)
                      ?? _workspaceSections.FirstOrDefault();
        if (section is not null)
        {
            SelectWorkspaceSection(section, focusRail);
        }
    }

    private void SyncSectionSelection()
    {
        if (SectionRail is null)
        {
            return;
        }

        var selected = _workspaceSections.FirstOrDefault(section => section.Page == _navigation.Page
                                                                    && section.PluginSection == _navigation.SectionId)
                           ?.Key
                       ?? SelectedSections.GetValueOrDefault(_navigation.Destination);
        foreach (var button in SectionRail.Children.OfType<Button>())
        {
            button.Classes.Set("selected", Equals(button.Tag, "rail." + selected));
        }
    }

    internal bool NavigateWorkspace(NavigationDirection direction)
    {
        if (HasActiveSurface || FocusManager?.GetFocusedElement() is not Control focused
                             || !focused.GetVisualAncestors().Contains(SectionRail))
        {
            return false;
        }

        if (direction == NavigationDirection.Right)
        {
            var section = _workspaceSections.FirstOrDefault(entry => Equals(focused.Tag, "rail." + entry.Key));
            if (section is not null)
            {
                SelectWorkspaceSection(section, false);
                FocusFirstControl(ActiveSubView?.Host ?? DestinationPanel());
            }

            return true;
        }

        return direction == NavigationDirection.Left;
    }

    private void OnHeaderBrightness(object? sender, RoutedEventArgs e)
    {
        ShowBrightnessSurface();
    }

    private void OnHeaderKeyboard(object? sender, RoutedEventArgs e)
    {
        OnScreenKeyboard(sender, e);
    }

    private sealed record WorkspaceSection(
        string Key,
        string Title,
        Geometry? Icon,
        OverlayPage Page,
        string? PluginSection,
        DeviceOverlaySection? DeviceSection);
}
