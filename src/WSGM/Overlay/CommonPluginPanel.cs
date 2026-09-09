using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Host-rendered common plugin controls. Only an explicit button press dispatches an action.</summary>
internal sealed class CommonPluginPanel : StackPanel
{
    private readonly ICommonPluginOverlaySource _source;
    private PluginOverlayInstance[] _observed = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<Action> _refresh = [];
    private readonly CancellationTokenSource _closed = new();
    private string _structure = "\0";
    private readonly PluginWidgetPin? _widget;
    private readonly bool _pinsOnly;
    private readonly Action<PluginWidgetPin, string>? _navigate;
    private readonly Dictionary<(string Plugin, string Instance, string Category), Control> _categories = [];

    internal CommonPluginPanel(ICommonPluginOverlaySource source, PluginWidgetPin? widget = null,
        Action<PluginWidgetPin, string>? navigate = null, bool pinsOnly = false)
    {
        _source = source;
        _widget = widget;
        _pinsOnly = pinsOnly;
        _navigate = navigate;
        Spacing = 8;
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { Refresh(); _timer.Start(); };
        DetachedFromVisualTree += (_, _) => { _timer.Stop(); _closed.Cancel(); };
    }

    internal void Refresh()
    {
        var instances = _source.Snapshot();
        _observed = instances;
        if (_widget is { } pin)
        {
            instances = instances.Where(instance => instance.Identity.PluginId == pin.PluginId
                && instance.Identity.InstanceId == pin.InstanceId).ToArray();
        }
        string structure = string.Join("|", instances.Select(instance =>
            $"{instance.Identity}:{instance.Generation}:{instance.Controls is not null}:{instance.Error}"));
        if (structure != _structure)
        {
            _structure = structure;
            Children.Clear();
            _refresh.Clear();
            _categories.Clear();
            foreach (var instance in instances) { AddInstance(instance); }
            if (_widget is not null && instances.Length == 0)
            { Children.Add(new TextBlock { Text = "Plugin unavailable", Classes = { "caption" } }); }
        }
        IsVisible = _widget is not null || instances.Length > 0;
        foreach (var update in _refresh) { update(); }
    }

    private void AddInstance(PluginOverlayInstance instance)
    {
        Children.Add(new TextBlock { Text = $"{instance.Name} / {instance.Identity.InstanceId}", Classes = { "eyebrow" } });
        var health = new TextBlock { Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Children.Add(health);
        var owner = instance;
        _refresh.Add(() => health.Text = _observed.FirstOrDefault(value => value.Identity == instance.Identity) is { } current ? current.Error ?? current.Status : "Plugin unavailable");
        if (owner.Controls is not { } actions) { return; }
        if (_widget is { } pinned)
        {
            var widget = actions.Widgets.FirstOrDefault(item => item.Id == pinned.WidgetId);
            if (widget is null) { Children.Add(new TextBlock { Text = "Widget unavailable" }); return; }
            Children.Add(new TextBlock { Text = widget.Label, Classes = { "setting-title" } });
            if (widget.NavigationCategory is { } category && _navigate is not null)
            {
                Button open = new() { Content = "Open plugin controls" };
                open.Click += (_, _) => _navigate(pinned, category);
                Children.Add(open);
            }
            int firstControl = Children.Count;
            foreach (var id in widget.ContributionIds)
            { AddContribution(instance, owner, actions.Contributions.First(item => item.Id == id)); }
            var controls = Children.Skip(firstControl).ToArray();
            var secondary = new TextBlock { Classes = { "caption" } };
            Children.Add(secondary);
            _refresh.Add(() =>
            {
                var states = _source.State(instance.Identity).Where(value => value.Generation == owner.Generation).ToArray();
                bool Predicate(string? key) => key is null || states.FirstOrDefault(value => value.Key == key)?.Value.Boolean == true;
                bool available = Predicate(widget.VisibleStateKey);
                bool enabled = available && Predicate(widget.EnabledStateKey);
                foreach (var control in controls) { control.IsEnabled = enabled; }
                secondary.Text = !available ? "Widget unavailable" : widget.SecondaryStateKey is { } key
                    ? states.FirstOrDefault(value => value.Key == key) is { } state ? Format(state.Value) : "No confirmed value" : "";
            });
            return;
        }
        foreach (var widget in actions.Widgets)
        {
            PluginWidgetPin pin = new(instance.Identity.PluginId, instance.Identity.InstanceId, widget.Id);
            Children.Add(new PluginWidgetPinControls(widget.Label,
                pinned => CommonPluginOverlaySource.SetPinnedAsync(pin, pinned)));
        }
        if (_pinsOnly) { return; }
        foreach (var group in actions.Contributions.GroupBy(contribution => contribution.Category))
        {
            TextBlock anchor = new() { Text = group.Key, Classes = { "caption" }, Focusable = true };
            _categories[(instance.Identity.PluginId, instance.Identity.InstanceId, group.Key)] = anchor;
            Children.Add(anchor);
            foreach (var contribution in group) { AddContribution(instance, owner, contribution); }
        }
    }

    internal void FocusCategory(PluginWidgetPin pin, string category)
    {
        Refresh();
        if (_categories.TryGetValue((pin.PluginId, pin.InstanceId, category), out var anchor))
        { anchor.BringIntoView(); anchor.Focus(); }
    }

    private void AddContribution(PluginOverlayInstance instance, PluginOverlayInstance owner, PluginUiContribution contribution)
    {
        long generation = owner.Generation;
        var row = new StackPanel { Spacing = 4 };
        var effective = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } };
        row.Children.Add(new TextBlock { Text = contribution.Label, Classes = { "setting-title" } });
        if (contribution.StateKey is not null) { row.Children.Add(effective); }
        Children.Add(row);
        _refresh.Add(() =>
        {
            var state = _source.State(instance.Identity).FirstOrDefault(value => value.Key == contribution.StateKey && value.Generation == generation);
            effective.Text = state is null ? "No confirmed value" : Format(state.Value);
        });
        if (contribution.Kind == PluginUiKind.Status) { return; }
        var inputs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (contribution.Kind == PluginUiKind.Action
            && owner.Controls!.Actions.First(value => value.Id == contribution.ActionId).Arguments.Count > 0)
        {
            row.Children.Add(new Expander { Header = "Edit and run", Content = inputs });
        }
        else { row.Children.Add(inputs); }
        Dictionary<string, Func<PluginValue>> argumentReaders = [];
        if (contribution.Kind == PluginUiKind.Action)
        {
            inputs.Orientation = Orientation.Vertical;
            var action = owner.Controls!.Actions.First(value => value.Id == contribution.ActionId);
            foreach (var field in action.Arguments)
            {
                inputs.Children.Add(new TextBlock { Text = field.Label, Classes = { "caption" } });
                if (field.Kind == PluginSettingKind.Boolean)
                {
                    var editor = new ToggleSwitch { IsChecked = field.Default.Boolean };
                    inputs.Children.Add(editor);
                    argumentReaders.Add(field.Key, () => new(Boolean: editor.IsChecked == true));
                }
                else if (field.Kind == PluginSettingKind.Number)
                {
                    var (editor, readArgument) = CreateTextArgumentEditor(field);
                    inputs.Children.Add(editor);
                    argumentReaders.Add(field.Key, readArgument);
                }
                else if (field.Choices is { } choices)
                {
                    var editor = new ComboBox { ItemsSource = choices, SelectedItem = field.Default.Text };
                    inputs.Children.Add(editor);
                    argumentReaders.Add(field.Key, () => new(Text: editor.SelectedItem as string ?? ""));
                }
                else
                {
                    var (editor, readArgument) = CreateTextArgumentEditor(field);
                    inputs.Children.Add(editor);
                    argumentReaders.Add(field.Key, readArgument);
                }
            }
        }
        Func<PluginValue?> read = () => null;
        var argument = owner.Controls!.Actions.First(action => action.Id == contribution.ActionId)
            .Arguments.FirstOrDefault(value => value.Key == contribution.ArgumentKey);
        if (contribution.Kind == PluginUiKind.Toggle)
        {
            var editor = new ToggleSwitch { IsChecked = argument!.Default.Boolean, OnContent = "On", OffContent = "Off" };
            inputs.Children.Add(editor);
            read = () => new PluginValue(Boolean: editor.IsChecked == true);
        }
        else if (contribution.Kind == PluginUiKind.Slider)
        {
            var editor = new Slider
            {
                Width = 180,
                Minimum = argument!.Minimum!.Value,
                Maximum = argument.Maximum!.Value,
                Value = argument.Default.Number!.Value
            };
            inputs.Children.Add(editor);
            read = () => new PluginValue(Number: editor.Value);
        }
        // The draft is intentionally separate from readback. Refresh cannot invoke the write path.
        var apply = new Button
        {
            Content = contribution.Kind == PluginUiKind.Action ? contribution.Label : "Apply",
            Tag = $"plugin.{instance.Identity.PluginId}.{instance.Identity.InstanceId}.{contribution.Id}"
        };
        inputs.Children.Add(apply);
        var result = new TextBlock { Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        row.Children.Add(result);
        bool busy = false;
        _refresh.Add(() => apply.IsEnabled = !busy && _observed.Any(value => value.Identity == instance.Identity && value.CanInvoke && value.Generation == generation));
        apply.Click += async (_, _) =>
        {
            if (busy || _closed.IsCancellationRequested) { return; }
            var current = _source.Snapshot().FirstOrDefault(value => value.Identity == instance.Identity);
            if (current is null || !current.CanInvoke || current.Generation != generation)
            { result.Text = "Widget unavailable. Select it again."; return; }
            if (_widget is { } pin)
            {
                var widget = current.Controls?.Widgets.FirstOrDefault(value => value.Id == pin.WidgetId);
                var states = _source.State(instance.Identity);
                bool Available(string? key) => key is null || states.Any(value => value.Generation == generation
                    && value.Key == key && value.Value.Boolean == true);
                if (widget is null || !Available(widget.VisibleStateKey) || !Available(widget.EnabledStateKey))
                { result.Text = "Widget unavailable."; return; }
            }
            busy = true;
            apply.IsEnabled = false;
            Dictionary<string, PluginValue> arguments = [];
            foreach (var field in argumentReaders) { arguments.Add(field.Key, field.Value()); }
            if (read() is { } value) { arguments.Add(contribution.ArgumentKey!, value); }
            result.Text = "Requested…";
            try
            {
                var response = await _source.InvokeAsync(instance.Identity, generation, contribution.ActionId!, arguments, _closed.Token);
                result.Text = $"{response.Outcome}: {response.Detail}";
            }
            catch (Exception ex) { result.Text = "Unconfirmed: " + ex.Message; }
            finally { busy = false; }
        };
    }

    private static string Format(PluginValue value) => value.Boolean is { } boolean ? (boolean ? "On" : "Off")
        : value.Number?.ToString("G", CultureInfo.CurrentCulture) ?? value.Text ?? "No confirmed value";

    internal static (Button Editor, Func<PluginValue> Read) CreateTextArgumentEditor(PluginSetting field)
    {
        string draft = field.Default.Text ?? field.Default.Number?.ToString("G", CultureInfo.CurrentCulture) ?? "";
        Button editor = new() { Content = draft.Length > 0 ? draft : "Enter value" };
        editor.Click += (_, _) =>
        {
            bool opened = KeyboardService.Request(field.Label, draft, field.Kind == PluginSettingKind.Number ? 64 : 4096, value =>
            {
                draft = value;
                editor.Content = draft.Length > 0 ? draft : "Enter value";
            });
            if (!opened) { editor.Content = "Keyboard unavailable. Reopen the overlay to retry."; }
        };
        return (editor, () => field.Kind == PluginSettingKind.Number
            ? new(Number: double.TryParse(draft, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) ? number : double.NaN)
            : new(Text: draft));
    }
}
