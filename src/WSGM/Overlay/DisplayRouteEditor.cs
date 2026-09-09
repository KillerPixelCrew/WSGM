using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Edits lifecycle policy and captures display observations without executing route actions.</summary>
internal sealed class DisplayRouteEditor : StackPanel
{
    private readonly DisplayRouteEditorServices _services;
    private readonly CheckBox _enabled = new() { Content = "Enable route automation" };
    private readonly ComboBox _event = new()
    {
        ItemsSource = new[] { "Enter Game Mode", "Leave Game Mode", "Desktop startup", "Desktop wake" },
        SelectedIndex = 0,
    };
    private readonly ComboBox _action = new();
    private readonly ComboBox _target = new();
    private readonly StackPanel _arguments = new() { Spacing = 4 };
    private readonly TextBlock _profileLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Slider _timeout = new() { Minimum = 1, Maximum = 120, Value = 30, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Dictionary<string, Func<PluginValue>> _readers = [];
    private DisplayRouteConfiguration _config = new();
    private DisplayRouteActionOption[] _options = [];
    private DisplayRouteBinding? _original;
    private DisplayProfile? _profile;
    private DisplayTargetIdentity[] _targets = [];
    private bool _loading, _busy, _closed;

    internal DisplayRouteEditor(DisplayRouteEditorServices services)
    {
        _services = services;
        Spacing = 8;
        Children.Add(new TextBlock
        {
            Text = "Bind session events to plugin actions. Capture each display layout while it is active. Save each event before selecting another; saving does not run an action.",
            TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(_enabled);
        Label("Lifecycle event", _event);
        Label("Plugin action", _action);
        Children.Add(_arguments);
        Label("Wait for display (Game Mode entry)", _target);
        Children.Add(_profileLabel);
        AddButton("Capture current display profile", async () =>
        {
            _profile = await _services.Capture();
            PopulateTargets(_profile.Targets.ToArray(), _profile.Targets.FirstOrDefault());
            ShowProfile();
            _status.Text = "Display profile captured in this draft. Save to keep it.";
        });
        AddButton("Clear display profile", () =>
        {
            _profile = null;
            PopulateTargets([], null);
            ShowProfile();
            return Task.CompletedTask;
        });
        Label("Timeout (seconds)", _timeout);
        var timeoutValue = new TextBlock();
        _timeout.PropertyChanged += (_, change) =>
        {
            if (change.Property == Slider.ValueProperty) { timeoutValue.Text = $"{_timeout.Value:0} seconds"; }
        };
        timeoutValue.Text = "30 seconds";
        Children.Add(timeoutValue);
        AddButton("Save route", () => SaveAsync(false));
        AddButton("Clear event binding", () => SaveAsync(true));
        AddButton("Reload routes and actions", ReloadAsync);
        Children.Add(_status);
        _event.SelectionChanged += (_, _) => { if (!_loading) { LoadBinding(); } };
        _action.SelectionChanged += (_, _) => { if (!_loading) { BuildArguments(null); } };
        AttachedToVisualTree += async (_, _) => await RunAsync(ReloadAsync);
        DetachedFromVisualTree += (_, _) => _closed = true;
    }

    private void Label(string text, Control control)
    {
        Children.Add(new TextBlock { Text = text, Classes = { "caption" } });
        Children.Add(control);
    }

    private void AddButton(string label, Func<Task> action)
    {
        Button button = new() { Content = label };
        button.Click += async (_, _) => await RunAsync(action);
        Children.Add(button);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) { return; }
        _busy = true;
        IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { if (!_closed) { _status.Text = ex.Message; } }
        finally { _busy = false; if (!_closed) { IsEnabled = true; } }
    }

    private async Task ReloadAsync()
    {
        var options = _services.Actions();
        var config = await _services.Read();
        if (_closed) { return; }
        _options = options;
        _config = config;
        _enabled.IsChecked = config.Enabled;
        LoadBinding();
    }

    private void LoadBinding()
    {
        _loading = true;
        try
        {
            _original = DisplayRouteEditorSource.Get(_config, _event.SelectedIndex);
            _profile = _original?.Profile;
            List<object> actions = ["No plugin action", .. _options];
            var selected = _options.FirstOrDefault(option => option.Identity == _original?.Plugin && option.Action.Id == _original?.ActionId);
            if (_original?.Plugin is { } missing && selected is null)
            { actions.Add($"Unavailable: {missing.PluginId} / {missing.InstanceId}: {_original.ActionId}"); }
            _action.ItemsSource = actions;
            _action.SelectedItem = selected is not null ? selected : _original?.Plugin is not null ? actions[^1] : actions[0];
            PopulateTargets(_profile?.Targets.ToArray() ?? [], _original?.Target);
            _timeout.Value = _original?.TimeoutSeconds ?? 30;
            BuildArguments(_original?.Arguments);
            ShowProfile();
            _status.Text = "";
        }
        finally { _loading = false; }
    }

    private void PopulateTargets(DisplayTargetIdentity[] targets, DisplayTargetIdentity? selected)
    {
        _targets = selected is not null && !targets.Any(target => target.Matches(selected)) ? [.. targets, selected] : targets;
        _target.ItemsSource = new[] { "No display wait" }.Concat(_targets.Select((target, index) => $"{index + 1}: {target.FriendlyName}")).ToArray();
        _target.SelectedIndex = selected is null ? 0 : Array.FindIndex(_targets, target => target.Matches(selected)) + 1;
        _target.IsEnabled = _event.SelectedIndex == 0;
    }

    private void ShowProfile() => _profileLabel.Text = _profile is null ? "No display profile saved for this event."
        : "Display profile: " + string.Join(", ", _profile.Targets.Select(target => target.FriendlyName));

    private void BuildArguments(IReadOnlyDictionary<string, PluginValue>? saved)
    {
        _arguments.Children.Clear();
        _readers.Clear();
        if (_action.SelectedItem is not DisplayRouteActionOption option) { return; }
        foreach (var field in option.Action.Arguments)
        {
            var value = saved?.GetValueOrDefault(field.Key) ?? field.Default;
            _arguments.Children.Add(new TextBlock { Text = field.Label, Classes = { "caption" } });
            if (field.Kind == PluginSettingKind.Boolean)
            {
                CheckBox editor = new() { Content = field.Label, IsChecked = value.Boolean };
                _arguments.Children.Add(editor);
                _readers[field.Key] = () => new(Boolean: editor.IsChecked == true);
            }
            else if (field.Choices is { } choices)
            {
                ComboBox editor = new() { ItemsSource = choices, SelectedItem = value.Text };
                _arguments.Children.Add(editor);
                _readers[field.Key] = () => new(Text: editor.SelectedItem as string ?? "");
            }
            else
            {
                var (editor, read) = CommonPluginPanel.CreateTextArgumentEditor(field with { Default = value });
                _arguments.Children.Add(editor);
                _readers[field.Key] = read;
            }
        }
    }

    private async Task SaveAsync(bool clear)
    {
        DisplayRouteBinding? binding = null;
        if (!clear)
        {
            var option = _action.SelectedItem as DisplayRouteActionOption;
            bool retainUnavailable = option is null && _action.SelectedIndex > 0;
            binding = new()
            {
                Plugin = option?.Identity ?? (retainUnavailable ? _original?.Plugin : null),
                ActionId = option?.Action.Id ?? (retainUnavailable ? _original?.ActionId : null),
                Arguments = retainUnavailable ? new(_original!.Arguments) : _readers.ToDictionary(pair => pair.Key, pair => pair.Value()),
                Profile = _profile,
                Target = _target.SelectedIndex > 0 ? _targets[_target.SelectedIndex - 1] : null,
                TimeoutSeconds = (int)_timeout.Value,
            };
            if (option is not null && option.Action.Arguments.Any(field =>
                !PluginConfigurationRules.Accepts(field, binding.Arguments.GetValueOrDefault(field.Key, field.Default))))
            { throw new ArgumentException("An action value is outside its declared range or choices."); }
            _ = DisplayRoutePlan.FromBinding(binding);
        }
        int index = _event.SelectedIndex;
        bool enabled = _enabled.IsChecked == true;
        await _services.Save(enabled, index, binding);
        if (_closed) { return; }
        _config.Enabled = enabled;
        DisplayRouteEditorSource.Set(_config, index, binding);
        LoadBinding();
        _status.Text = clear ? "Event binding cleared." : "Route saved. It will run on the configured lifecycle event.";
    }
}
