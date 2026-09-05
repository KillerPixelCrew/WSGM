using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Windows power-profile picker for the overlay. It reports intent through its model
/// and never acquires native services or writes configuration itself.</summary>
public sealed class PowerSchemeView : UserControl
{
    private readonly ComboBox _profiles = new()
    {
        DisplayMemberBinding = new Binding(nameof(PowerScheme.Name)),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        Tag = "system.power-profile.choice",
    };
    private readonly Button _apply = new() { Content = "Apply", Tag = "system.power-profile.apply" };
    private readonly Button _refresh = new() { Content = "Refresh", Tag = "system.power-profile.refresh" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
    private PowerSchemeSelection? _model;
    private IReadOnlyList<PowerScheme>? _items;

    /// <summary>Creates persistent controls so telemetry updates cannot interrupt a selection.</summary>
    public PowerSchemeView()
    {
        AutomationProperties.SetName(_profiles, "Windows power profile");
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        choices.Children.Add(_profiles);
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
                    new TextBlock { Text = "Windows power profile", Classes = { "setting-title" } },
                    choices,
                    _status,
                },
            },
        };
        _profiles.SelectionChanged += (_, _) => UpdateButtons();
        _apply.Click += async (_, _) =>
        {
            if (_model is { } model && _profiles.SelectedItem is PowerScheme choice)
            {
                await model.ApplyAsync(choice.Id);
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

    internal void Attach(PowerSchemeSelection? model)
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
        if (!ReferenceEquals(_items, _model?.Schemes))
        {
            _items = _model?.Schemes;
            _profiles.ItemsSource = _items?.Select(scheme => _items.Count(other => other.Name == scheme.Name) > 1
                ? scheme with { Name = $"{scheme.Name} ({scheme.Id:D})" } : scheme).ToArray();
            _profiles.SelectedIndex = -1;
            if (_items is not null)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == _model?.ActiveId)
                    {
                        _profiles.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
        _status.Text = _model?.Status ?? "Power profiles are unavailable.";
        if (_model?.ActiveId is null)
        {
            _profiles.SelectedIndex = -1;
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _profiles.IsEnabled = _model?.CanSelect is true;
        _apply.IsEnabled = _model?.CanSelect is true && _profiles.SelectedItem is PowerScheme choice
            && choice.Id != _model.ActiveId;
        _refresh.IsEnabled = _model is { Busy: false };
    }
}
