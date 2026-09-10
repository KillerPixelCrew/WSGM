using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Processor core-preference picker for the overlay. It reports intent through its model
/// and never acquires native services or writes configuration itself.</summary>
public sealed class HybridCoreView : UserControl
{
    private readonly ComboBox _modes = new()
    {
        DisplayMemberBinding = new Binding(nameof(HybridCoreOption.Name)),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        Tag = "system.hybrid-cores.choice",
    };
    private readonly Button _apply = new() { Content = "Apply", Tag = "system.hybrid-cores.apply" };
    private readonly Button _refresh = new() { Content = "Refresh", Tag = "system.hybrid-cores.refresh" };
    private readonly TextBlock _effect = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private HybridCoreSelection? _model;
    private IReadOnlyList<HybridCoreOption>? _items;

    /// <summary>Creates persistent controls so a refresh cannot interrupt a selection.</summary>
    public HybridCoreView()
    {
        AutomationProperties.SetName(_modes, "Processor core preference");
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        choices.Children.Add(_modes);
        Grid.SetColumn(_apply, 1);
        choices.Children.Add(_apply);
        Grid.SetColumn(_refresh, 2);
        choices.Children.Add(_refresh);
        Content = new Border
        {
            Classes = { "tile" },
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Processor core preference", Classes = { "setting-title" } },
                    choices,
                    _effect,
                    _status,
                },
            },
        };
        _modes.SelectionChanged += (_, _) =>
        {
            UpdateEffect();
            UpdateButtons();
        };
        _apply.Click += async (_, _) =>
        {
            if (_model is { } model && _modes.SelectedItem is HybridCoreOption choice)
            {
                await model.ApplyAsync(choice.Mode);
            }
        };
        _refresh.Click += async (_, _) =>
        {
            if (_model is { } model)
            {
                await model.RefreshAsync();
            }
        };
        Render();
    }

    internal void Attach(HybridCoreSelection? model)
    {
        if (_model is not null)
        {
            _model.Changed -= Render;
        }
        _model = model;
        _items = null;
        if (model is not null)
        {
            model.Changed += Render;
        }
        Render();
    }

    private void Render()
    {
        if (!ReferenceEquals(_items, _model?.Options))
        {
            _items = _model?.Options;
            _modes.ItemsSource = _items;
            _modes.SelectedIndex = -1;
        }

        // The plugged-in value is what the list follows. When the two sources disagree the status
        // line says so, and applying writes both, so one selection can still represent the pair.
        if (_items is not null && _model?.Status.OnAc is { } active)
        {
            for (int index = 0; index < _items.Count; index++)
            {
                if (_items[index].Mode == active)
                {
                    _modes.SelectedIndex = index;
                    break;
                }
            }
        }
        else
        {
            _modes.SelectedIndex = -1;
        }

        _status.Text = _model?.Detail ?? "The processor core preference is unavailable.";
        UpdateEffect();
        UpdateButtons();
    }

    private void UpdateEffect() =>
        _effect.Text = _modes.SelectedItem is HybridCoreOption choice ? choice.Description : string.Empty;

    private void UpdateButtons()
    {
        _modes.IsEnabled = _model?.CanSelect is true && _items?.Count > 0;
        _apply.IsEnabled = _model?.CanSelect is true
            && _modes.SelectedItem is HybridCoreOption choice
            && (_model.Status.OnAc != choice.Mode || _model.Status.OnBattery != choice.Mode);
        _refresh.IsEnabled = _model is { Busy: false };
    }
}
