using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using WSGM.Controls;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly Dictionary<string, FAInfoBadge> _sectionBadges = [];
    private readonly List<WorkspaceSection> _workspaceSections = [];
    private OverlayViewModel ViewModel => (OverlayViewModel)DataContext!;

    private Button? SelectedSectionButton => SectionRail.Children.OfType<Button>()
        .FirstOrDefault(button => button.Classes.Contains("selected"));

    private void OnWorkspacePolicyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed || e.PropertyName is not (
                nameof(OverlayViewModel.ShowThemes) or nameof(OverlayViewModel.ShowAnimations)
                or nameof(OverlayViewModel.ShowSounds) or nameof(OverlayViewModel.ShowArtwork)
                or nameof(OverlayViewModel.ShowGameLibrary) or nameof(OverlayViewModel.ShowEmulators)
                or nameof(OverlayViewModel.ShowGameTools)))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
            {
                RefreshWorkspace();
            }
        });
    }

    private void RefreshWorkspace()
    {
        // The pinned section cards follow the same availability as the rail entries they open.
        _pins.RefreshSections();
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
            RefreshDeviceSectionRail(DescribeDevicePages(DeviceSnapshotOrOff(), _performanceSource?.Snapshot()));
            return;
        }

        var sections = new List<WorkspaceSection>();
        if (_navigation.Destination == OverlayDestination.QuickAccess)
        {
            sections.Add(new WorkspaceSection("quick-access", "Pinned controls", Icons.Pin,
                OverlayPage.QuickAccess, null));
        }
        else
        {
            foreach (var view in SubViews.Where(view => view.Destination == _navigation.Destination
                                                        && view.Title is not null &&
                                                        (view.Available?.Invoke() ?? true)))
            {
                sections.Add(new WorkspaceSection(view.Page.ToString(), view.Title!,
                    view.Icon, view.Page, null));
            }
        }

        ReconcileSectionRail(sections);
    }

    /// <summary>Shows the Overview, every section page with rows and the GPU page on the rail, with row counts.</summary>
    /// <param name="pages">The Overview, then the section pages, as described for this pass.</param>
    private void RefreshDeviceSectionRail(IReadOnlyList<DevicePage> pages)
    {
        if (_navigation.Destination != OverlayDestination.Device)
        {
            return;
        }

        var sections = pages.Where(page => page.SectionId is not null && page.Count > 0).ToArray();
        var entries = new List<WorkspaceSection>
        {
            new(DevicePages.OverviewKey, "Overview", Icons.Gear, OverlayPage.Device, null)
        };
        entries.AddRange(sections.Select(page => new WorkspaceSection(page.RailKey, page.Title,
            SectionIconFor(page.Icon) ?? Icons.ListLines, OverlayPage.DevicePluginSection, page.SectionId)));
        entries.Add(new WorkspaceSection(DevicePages.GpuKey, "GPU", Icons.Monitor, OverlayPage.DeviceGpu, null));
        ReconcileSectionRail(entries);
        foreach (var page in sections)
        {
            if (_sectionBadges.TryGetValue(page.RailKey, out var badge))
            {
                badge.Value = page.Count;
                badge.IsVisible = true;
            }
        }
    }

    private void ReconcileSectionRail(IReadOnlyList<WorkspaceSection> entries)
    {
        var focusedKey = CurrentSemanticFocusKey();
        // Values never participate in this comparison. Telemetry must not replace rail focus targets.
        if (!_workspaceSections.SequenceEqual(entries))
        {
            // An unchanged entry keeps its button, so a section appearing or leaving does not take focus
            // from the others; a new or changed entry gets a new button.
            Dictionary<WorkspaceSection, Control> previous = [];
            for (var index = 0; index < _workspaceSections.Count && index < SectionRail.Children.Count; index++)
            {
                previous.TryAdd(_workspaceSections[index], SectionRail.Children[index]);
            }

            _workspaceSections.Clear();
            _workspaceSections.AddRange(entries);
            List<Control> rows = [];
            Control? created = null;
            foreach (var entry in entries)
            {
                if (!previous.Remove(entry, out var button))
                {
                    _sectionBadges.Remove(entry.Key);
                    button = CreateSectionButton(entry);
                    if (Equals(button.Tag, focusedKey))
                    {
                        created = button;
                    }
                }

                rows.Add(button);
            }

            foreach (var gone in previous.Keys.Where(gone => entries.All(entry => entry.Key != gone.Key)))
            {
                _sectionBadges.Remove(gone.Key);
            }

            DeviceGroupRenderer.ReconcileChildren(SectionRail, rows);
            created?.Focus(NavigationMethod.Directional);
        }

        if (ActiveSubView is { Available: { } availability } && !availability())
        {
            Surfaces.CloseAll();
            LeaveAllNestedPages();
        }

        var selectedKey = _session.Sections.GetValueOrDefault(_navigation.Destination);
        if (selectedKey is not null && entries.All(entry => entry.Key != selectedKey)
                                    && entries.FirstOrDefault() is { } fallback)
        {
            // The descriptor owner retracted the open page. Retire its text-entry callback
            // before replacing the route, then land on the surviving overview rail.
            Surfaces.CloseAll();
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
            Data = entry.Icon,
            Width = 20,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("DeckSecondaryBrush"));
        Grid.SetColumn(icon, 1);
        face.Children.Add(icon);
        var label = new TextBlock
        {
            Text = entry.Title,
            FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
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
        if (Surfaces.HasActive)
        {
            return;
        }

        LeaveAllNestedPages();
        _navigation.Select(_navigation.Destination);
        _session.Sections[_navigation.Destination] = section.Key;
        if (section.Page is not (OverlayPage.QuickAccess or OverlayPage.Device))
        {
            EnterSubView(section.Page, section.PluginSection);
        }

        ContentScroller.Offset = default;
        SyncSectionSelection();
        if (focusRail)
        {
            SelectedSectionButton?.Focus(NavigationMethod.Directional);
        }
    }

    private void SelectRememberedSection(bool focusRail)
    {
        var remembered = _session.Sections.GetValueOrDefault(_navigation.Destination);
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
                       ?? _session.Sections.GetValueOrDefault(_navigation.Destination);
        foreach (var button in SectionRail.Children.OfType<Button>())
        {
            button.Classes.Set("selected", Equals(button.Tag, "rail." + selected));
        }
    }

    internal bool NavigateWorkspace(NavigationDirection direction)
    {
        if (!Surfaces.HasActive && FocusManager?.GetFocusedElement() is Control sectionFocus
                              && sectionFocus.GetVisualAncestors().OfType<CollapsibleSection>().FirstOrDefault() is
                              { } fold
                              && ReferenceEquals(sectionFocus, fold.Heading)
                              && direction is NavigationDirection.Left or NavigationDirection.Right)
        {
            fold.IsExpanded = direction == NavigationDirection.Right;
            return true;
        }

        if (Surfaces.HasActive || FocusManager?.GetFocusedElement() is not Control focused
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
                if ((ActiveSubView?.Host ?? DestinationPanel()) is { } shown)
                {
                    FocusFirstControl(shown);
                }
            }

            return true;
        }

        return direction == NavigationDirection.Left;
    }

    private void OnHeaderBrightness(object? sender, RoutedEventArgs e)
    {
        Surfaces.ShowBrightness();
    }

    private sealed record WorkspaceSection(
        string Key,
        string Title,
        Geometry? Icon,
        OverlayPage Page,
        string? PluginSection);
}
