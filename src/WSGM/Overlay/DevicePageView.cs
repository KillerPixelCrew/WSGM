using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Device destination's controls pane: the Overview, each section page and the GPU page, each drawn from
///     its <see cref="DevicePage" /> description, with the device status, the missing-prerequisites banner and the
///     Quick Access pin choices around them.
/// </summary>
/// <remarks>
///     The window owns navigation and decides which description to show; this view owns the controls and keeps
///     focus, drafts and folds through every redraw. Device pages keep the deck's reading width; the GPU page,
///     whose groups are wide tables of settings, uses the whole pane.
/// </remarks>
public sealed class DevicePageView : Panel
{
    private readonly StackPanel _device = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _gpu = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _gpuGroups = new() { Spacing = 12 };
    private readonly TextBlock _gpuNote = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _groups = new() { Spacing = 12 };
    private readonly StackPanel _pins = new() { Spacing = 8 };

    private readonly TextBlock _status = new()
    {
        FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 2, 0)
    };

    private readonly TextBlock _statusDetail = new()
    {
        Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6)
    };

    private CollapsibleSection? _pinsFold;
    private DevicePrerequisiteSource? _prerequisites;

    // The integration switch and device the banner was last read for; nothing else changes its answer.
    private (bool Enabled, string Device)? _prerequisitesFor;

    private bool _prerequisitesReading;
    private bool _prerequisitesStale;
    private DeviceGroupRenderer? _renderer;

    /// <summary>Lays out the Device and GPU panes; <see cref="Configure" /> supplies the renderer.</summary>
    public DevicePageView()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _device.Bind(MaxWidthProperty, this.GetResourceObservable("DeckContentMaxWidth"));
        // A device package can arrive on an install that has no controller support, because the Plugins
        // folder is one an administrator can copy a package file into. Without this the package simply
        // does nothing and nothing says why. It sits on every Device page, which is where a person goes
        // when their device is not working.
        PrerequisiteBanner = new Border
        {
            Classes = { "device-group" }, IsVisible = false, Margin = new Thickness(2, 0, 2, 8),
            Child = PrerequisiteDetail
        };
        _device.Children.Add(_status);
        _device.Children.Add(_statusDetail);
        _device.Children.Add(PrerequisiteBanner);
        _device.Children.Add(_groups);
        Children.Add(_device);

        _gpu.Children.Add(new TextBlock { Text = "GPU", Classes = { "eyebrow" } });
        _gpu.Children.Add(new TextBlock
        {
            Text = "Graphics and driver controls for your GPU. Availability depends on the GPU, display and "
                   + "installed driver.",
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap
        });
        _gpu.Children.Add(_gpuNote);
        _gpu.Children.Add(_gpuGroups);
        Children.Add(_gpu);
        _gpu.IsVisible = false;
    }

    /// <summary>The banner explaining what a device package on this install is missing.</summary>
    internal Border PrerequisiteBanner { get; }

    /// <summary>The banner text.</summary>
    internal TextBlock PrerequisiteDetail { get; } = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>The Quick Access pin choices for Device groups, shown on the Overview.</summary>
    internal StackPanel SectionPins { get; } = new() { Spacing = 8 };

    /// <summary>The plugin widget pin toggles, shown on the Overview below the group choices.</summary>
    internal StackPanel WidgetPins { get; } = new() { Spacing = 8 };

    /// <summary>Completes when the current prerequisite read has been published to the banner.</summary>
    internal Task PrerequisitesRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>The rail key of the page shown last, or null before the first.</summary>
    internal string? Shown { get; private set; }

    /// <summary>The number of row controls on the page shown, for the render log.</summary>
    internal int RowCount => (_gpu.IsVisible ? _gpuGroups : _groups).GetLogicalDescendants()
        .Count(control => control is DeviceCapabilityControl);

    /// <summary>Supplies the renderer and the session's fold state, once, before the first page is shown.</summary>
    /// <param name="renderer">The renderer every Device description is drawn with.</param>
    /// <param name="expanded">The session's expanded fold keys.</param>
    internal void Configure(DeviceGroupRenderer renderer, ISet<string> expanded)
    {
        _renderer = renderer;
        _pins.Children.Add(new TextBlock
        {
            Text = "Choose sections and widgets to show on Quick Access.", Classes = { "caption" }
        });
        _pins.Children.Add(SectionPins);
        _pins.Children.Add(WidgetPins);
        _pinsFold = CollapsibleSection.Persisted("Quick Access pins", _pins, expanded, "device.quick-access-pins",
            "device.quick-access-pins.expand");
        _pinsFold.ExpansionChanged += _ => PinsExpanded?.Invoke();
        _device.Children.Add(_pinsFold);
    }

    /// <summary>Raised when the pin choices fold opens or closes.</summary>
    internal event Action? PinsExpanded;

    /// <summary>Draws a page from its description, keeping every group whose layout did not change.</summary>
    /// <param name="page">The page.</param>
    /// <returns>
    ///     False when focus was on a control this redraw removed and nothing on the page could take it back, so
    ///     the caller can return it to the section rail.
    /// </returns>
    internal bool Show(DevicePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var gpu = page.RailKey == DevicePages.GpuKey;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var owned = focused is not null && this.IsVisualAncestorOf(focused);
        var focusKey = focused?.Tag as string;
        var foldKey = focused?.GetVisualAncestors().OfType<CollapsibleSection>().FirstOrDefault()?.Heading.Tag;

        _device.IsVisible = !gpu;
        _gpu.IsVisible = gpu;
        var host = gpu ? _gpuGroups : _groups;
        if (gpu)
        {
            _gpuNote.Text = page.Note;
            _gpuNote.IsVisible = page.Note is { Length: > 0 };
        }
        else
        {
            _status.IsVisible = _statusDetail.IsVisible = page.Heading is not null;
            _status.Text = page.Heading?.Status;
            _statusDetail.Text = page.Heading?.Detail;
            _pinsFold!.IsVisible = page.RailKey == DevicePages.OverviewKey;
        }

        _renderer!.Reconcile(host, page.Groups, false);
        host.IsVisible = page.Groups.Count > 0;
        if (Shown != page.RailKey)
        {
            Shown = page.RailKey;
            // Groups shared between pages keep their controls, but the other page's are gone.
            _renderer.Reconcile(gpu ? _groups : _gpuGroups, [], false);
        }

        if (!owned || this.IsVisualAncestorOf(focused))
        {
            return true;
        }

        // A rebuilt group took the focused control with it: land on its successor, else on the heading of
        // the fold it was in.
        var target = host.GetLogicalDescendants().OfType<Control>()
                         .FirstOrDefault(control => control.Focusable && focusKey is not null
                                                                      && Equals(control.Tag, focusKey))
                     ?? host.GetLogicalDescendants().OfType<CollapsibleSection>()
                         .FirstOrDefault(section => foldKey is not null && Equals(section.Heading.Tag, foldKey))
                         ?.Heading;
        if (target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
        {
            target.Focus(NavigationMethod.Directional);
            return true;
        }

        return false;
    }

    /// <summary>Supplies the reader behind the missing-prerequisites banner and reads it.</summary>
    /// <param name="prerequisites">The reader, or null in a preview with no session behind it.</param>
    internal void AttachPrerequisites(DevicePrerequisiteSource? prerequisites)
    {
        _prerequisites = prerequisites;
        ReadPrerequisites();
    }

    /// <summary>Reads the prerequisites again when the integration switch or the device changed.</summary>
    /// <param name="device">The current Device snapshot.</param>
    /// <remarks>
    ///     The read checks files, a driver and the registry, and only the definition and the switch change
    ///     what it finds, so a value or telemetry change does not repeat it.
    /// </remarks>
    internal void FollowPrerequisites(DeviceOverlaySnapshot device)
    {
        (bool Enabled, string Device) subject = (device.Visible, device.Detail);
        if (_prerequisitesFor == subject)
        {
            return;
        }

        _prerequisitesFor = subject;
        ReadPrerequisites();
    }

    /// <summary>Renders what a device package on this install is missing, if anything.</summary>
    private void ReadPrerequisites()
    {
        // The read opens every package in the Plugins folder, so it runs on a worker and the banner
        // fills in when it lands. One read runs at a time; a refresh asked for meanwhile reads once
        // more after it.
        if (_prerequisites is not { } source)
        {
            ShowPrerequisites(new DevicePrerequisiteAdvice(""));
            return;
        }

        if (_prerequisitesReading)
        {
            _prerequisitesStale = true;
            return;
        }

        _prerequisitesReading = true;
        PrerequisitesRefresh = ReadPrerequisitesAsync(source);
        Log.Observe(PrerequisitesRefresh, "Overlay device prerequisites");
    }

    private async Task ReadPrerequisitesAsync(DevicePrerequisiteSource source)
    {
        try
        {
            var advice = await Task.Run(() => source.Read());
            if (ReferenceEquals(_prerequisites, source))
            {
                ShowPrerequisites(advice);
            }
        }
        finally
        {
            _prerequisitesReading = false;
            if (_prerequisitesStale)
            {
                _prerequisitesStale = false;
                ReadPrerequisites();
            }
        }
    }

    /// <summary>Says what a device package on this install is missing, if anything.</summary>
    /// <param name="advice">The advice from the last read.</param>
    private void ShowPrerequisites(DevicePrerequisiteAdvice advice)
    {
        // No page check: the banner is part of the Device pane, so that pane's own visibility is the gate.
        // It stays up on the Device sub-pages too, which is where someone hunting a dead device most likely
        // ends up.
        // The banner only explains: INV-020 keeps driver installation in setup, because the USB/IP
        // install restarts every USB 3.0 hub and would take the pad, the touch digitiser and the
        // keyboard away from whoever is holding the machine.
        PrerequisiteBanner.IsVisible = advice.HasAdvice;
        PrerequisiteDetail.Text = advice.Detail;
    }
}
