using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Tools > Display: panel brightness and the display mode, as one pinnable, folding group.</summary>
/// <remarks>
///     The page shows its plain heading until the brightness owner attaches. It then takes the pinnable
///     heading, and folds once the window opens, so the fold's persisted expansion is read once.
/// </remarks>
public sealed partial class DisplayPage : StackPanel
{
    private Func<string?>? _display;
    private DisplayModeAccess? _displayModes;
    private NativeQamBrightnessService? _service;

    /// <summary>Builds the page with its plain heading.</summary>
    public DisplayPage()
    {
        InitializeComponent();
    }

    /// <summary>Shows the brightness and display mode under a pinnable heading.</summary>
    /// <param name="service">The brightness owner.</param>
    /// <param name="displayModes">The display mode reader and writer.</param>
    /// <param name="display">The display the sheet is on.</param>
    /// <param name="header">The pinnable heading for <see cref="DevicePages.DisplayId" />.</param>
    internal void Attach(NativeQamBrightnessService service, DisplayModeAccess displayModes, Func<string?> display,
        Func<SectionPinHeader> header)
    {
        _service = service;
        _displayModes = displayModes;
        _display = display;
        DisplayBrightnessHost.Tag = DevicePages.DisplayId;
        // A shown page already owns a fold whose body contains this host. Replacing that fold would
        // detach the entire Display page instead of changing its heading.
        if (Children.FirstOrDefault() is not CollapsibleSection)
        {
            Children[0] = header();
        }

        DisplayBrightnessHost.Children.Clear();
        DisplayBrightnessHost.Children.Add(new DisplayBrightnessView(service));
        DisplayBrightnessHost.Children.Add(new DisplayModeView(displayModes.Read, displayModes.Apply, display,
            displayModes.Changes));
    }

    /// <summary>A new copy of the brightness and display mode for Quick access.</summary>
    /// <returns>The copy; it follows the same owners as the page.</returns>
    /// <exception cref="InvalidOperationException">The brightness owner has not attached.</exception>
    internal Control CreateCopy()
    {
        if (_service is null || _displayModes is not { } modes || _display is null)
        {
            throw new InvalidOperationException("The brightness owner has not attached.");
        }

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new DisplayBrightnessView(_service),
                new DisplayModeView(modes.Read, modes.Apply, _display, modes.Changes)
            }
        };
    }

    /// <summary>Folds the page under its pinnable heading, once its heading is in place.</summary>
    /// <param name="expanded">The session's expanded fold keys.</param>
    internal void Fold(ISet<string> expanded)
    {
        if (Children.FirstOrDefault() is not SectionPinHeader header)
        {
            return;
        }

        var body = new StackPanel { Spacing = Spacing, Tag = DevicePages.DisplayId };
        foreach (var child in Children.Skip(1).ToArray())
        {
            Children.Remove(child);
            body.Children.Add(child);
        }

        Children.Clear();
        // The fold's heading carries the title; the header keeps only its pin action there.
        header.ShowPinOnly();
        Children.Add(CollapsibleSection.Persisted(header.Title, body, expanded, DevicePages.DisplayId,
            DevicePages.DisplayId + ".expand", header));
    }
}
