using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Host-rendered common plugin controls. Only an explicit button press dispatches an action.</summary>
internal sealed class CommonPluginPanel : StackPanel
{
    private readonly CommonPluginOverlaySource _source;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<Action> _refresh = [];
    private readonly CancellationTokenSource _closed = new();
    private string _structure = "";

    internal CommonPluginPanel(CommonPluginOverlaySource source)
    {
        _source = source;
        Spacing = 8;
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { Refresh(); _timer.Start(); };
        DetachedFromVisualTree += (_, _) => { _timer.Stop(); _closed.Cancel(); };
    }

    private void Refresh()
    {
        var instances = _source.Snapshot();
        string structure = string.Join("|", instances.Select(instance =>
            $"{instance.Identity}:{instance.Registration?.Context.Generation}:{instance.Registration?.Actions is not null}:{instance.Error}"));
        if (structure != _structure)
        {
            _structure = structure;
            Children.Clear();
            _refresh.Clear();
            foreach (var instance in instances) { AddInstance(instance); }
        }
        IsVisible = instances.Length > 0;
        foreach (var update in _refresh) { update(); }
    }

    private void AddInstance(CommonPluginInstanceView instance)
    {
        Children.Add(new TextBlock { Text = $"{instance.Manifest.Name} / {instance.Identity.InstanceId}", Classes = { "eyebrow" } });
        var health = new TextBlock { Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Children.Add(health);
        var owner = instance.Registration;
        _refresh.Add(() => health.Text = instance.Error ?? (owner is null ? "Starting" : $"{owner.Health.Health}: {owner.Health.Detail}"));
        if (owner?.Actions is not { } actions) { return; }
        foreach (var group in actions.Contributions.GroupBy(contribution => contribution.Category))
        {
            Children.Add(new TextBlock { Text = group.Key, Classes = { "caption" } });
            foreach (var contribution in group) { AddContribution(instance, owner, contribution); }
        }
    }

    private void AddContribution(CommonPluginInstanceView instance, PluginRegistration owner, PluginUiContribution contribution)
    {
        long generation = owner.Context.Generation;
        var row = new StackPanel { Spacing = 4 };
        var effective = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } };
        row.Children.Add(new TextBlock { Text = contribution.Label, Classes = { "setting-title" } });
        row.Children.Add(effective);
        Children.Add(row);
        _refresh.Add(() =>
        {
            var state = _source.State(instance.Identity).FirstOrDefault(value => value.Key == contribution.StateKey && value.Generation == generation);
            effective.Text = state is null ? "No confirmed value" : Format(state.Value);
        });
        if (contribution.Kind == PluginUiKind.Status) { return; }
        var inputs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(inputs);
        Func<PluginValue?> read = () => null;
        var argument = owner.Actions!.Actions.First(action => action.Id == contribution.ActionId)
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
        _refresh.Add(() => apply.IsEnabled = !busy && !owner.IsStopping && !owner.Quarantined && owner.Context.Generation == generation);
        apply.Click += async (_, _) =>
        {
            if (busy) { return; }
            busy = true;
            apply.IsEnabled = false;
            Dictionary<string, PluginValue> arguments = [];
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
}
