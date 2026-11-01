using System;
using System.Collections.Generic;
using Avalonia.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Tools > Plugins: the controls the enabled common plugins publish, without the device's own.</summary>
/// <remarks>The page is offered only while a plugin panel has something to show, since an empty page is a dead end.</remarks>
public sealed partial class PluginsPage : StackPanel
{
    private CommonPluginPanel? _panel;

    /// <summary>Builds the page with no plugin source.</summary>
    public PluginsPage()
    {
        InitializeComponent();
    }

    /// <summary>Whether the rail offers the page.</summary>
    internal bool Offered { get; private set; }

    /// <summary>Raised when <see cref="Offered" /> changes after the plugin panel showed or hid itself.</summary>
    internal event Action? OfferedChanged;

    /// <summary>Shows a plugin source's own instances, or empties the page with null.</summary>
    /// <param name="source">The borrowed plugin projection, or null.</param>
    /// <param name="folds">The session's expanded fold keys.</param>
    internal void Attach(CommonPluginOverlaySource? source, ISet<string> folds)
    {
        Show(source is null
            ? null
            : new CommonPluginPanel(source, preferences: source.WidgetPreferences, folds: folds,
                independent: source.IndependentSnapshot));
    }

    /// <summary>Shows one plugin panel, or empties the page with null.</summary>
    /// <param name="panel">The panel the page offers while it has something to show.</param>
    internal void Show(CommonPluginPanel? panel)
    {
        CommonPluginRows.Children.Clear();
        _panel = panel;
        Offered = false;
        if (panel is null)
        {
            return;
        }

        CommonPluginRows.Children.Add(panel);
        Offered = panel.IsVisible;
        panel.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty)
            {
                Offered = panel.IsVisible;
                OfferedChanged?.Invoke();
            }
        };
    }

    /// <summary>Follows a plugin host change; the page reads no device.</summary>
    internal void Refresh()
    {
        _panel?.Refresh();
    }

    /// <summary>Moves focus to a pinned widget's category.</summary>
    /// <param name="pin">The widget.</param>
    /// <param name="category">Its category.</param>
    internal void FocusCategory(PluginWidgetPin pin, string category)
    {
        _panel?.FocusCategory(pin, category);
    }
}
