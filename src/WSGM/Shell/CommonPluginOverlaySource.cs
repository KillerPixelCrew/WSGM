using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

internal sealed record PluginOverlayControls(
    IReadOnlyList<PluginAction> Actions,
    IReadOnlyList<PluginUiContribution> Contributions,
    IReadOnlyList<PluginWidget> Widgets);

internal sealed record PluginOverlayInstance(
    PluginInstanceIdentity Identity,
    string Name,
    long Generation,
    PluginOverlayControls? Controls,
    string Status,
    bool CanInvoke,
    string? Error);

internal sealed record PluginWidgetPreferences(
    Func<Task<PluginWidgetPin[]>> Read,
    Func<PluginWidgetPin, bool, Task> Set,
    Func<PluginWidgetPin, int, Task> Move,
    Func<PluginWidgetPin, Task> Remove,
    Func<Task> Reset);

/// <summary>Read-only widget observations and explicit action routing, independent of package lifecycle.</summary>
internal interface ICommonPluginOverlaySource
{
    PluginOverlayInstance[] Snapshot();
    PluginStatePublication[] State(PluginInstanceIdentity identity);

    Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken);
}

/// <summary>Routes overlay intent to the resident common host without giving views lifecycle ownership.</summary>
internal sealed class CommonPluginOverlaySource : ICommonPluginOverlaySource
{
    private readonly PluginHost _host;
    private readonly CommonPluginManager? _manager;
    private readonly object _pinsGate = new();
    private PluginWidgetPin[] _pins;

    internal CommonPluginOverlaySource(CommonPluginManager? manager, PluginHost host,
        IReadOnlyList<PluginWidgetPin> pins, ICommonPluginOverlaySource? device = null)
    {
        _manager = manager;
        _host = host;
        Device = device;
        _pins = pins.ToArray();
        WidgetPreferences = new PluginWidgetPreferences(ReadPinsAsync, SetPinnedAsync, MovePinAsync,
            pin => SetPinnedAsync(pin, false), ResetPinOrderAsync);
    }

    internal ICommonPluginOverlaySource? Device { get; }

    internal PluginWidgetPreferences WidgetPreferences { get; }

    public PluginOverlayInstance[] Snapshot()
    {
        return
        [
            .. (_manager?.Snapshot() ?? []).Select(instance =>
            {
                var owner = instance.Registration;
                var actions = owner?.Actions;
                return new PluginOverlayInstance(instance.Identity, instance.Manifest.Name,
                    owner?.Context.Generation ?? 0,
                    actions is null
                        ? null
                        : new PluginOverlayControls(actions.Actions, actions.Contributions, actions.Widgets),
                    owner is null ? "Starting" : $"{owner.Health.Health}: {owner.Health.Detail}",
                    owner is { IsStopping: false, Quarantined: false }, instance.Error);
            }),
            .. Device?.Snapshot() ?? []
        ];
    }

    public PluginStatePublication[] State(PluginInstanceIdentity identity)
    {
        return Device?.Snapshot().Any(instance => instance.Identity == identity) == true
            ? Device.State(identity)
            : _host.StateSnapshot(identity);
    }

    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        return Device?.Snapshot().Any(instance => instance.Identity == identity) == true
            ? Device.InvokeAsync(identity, generation, action, arguments, cancellationToken)
            : _host.InvokeActionAsync(identity, generation, action, arguments, PluginActionOrigin.User,
                DateTimeOffset.UtcNow.AddSeconds(10), cancellationToken);
    }

    internal void ApplyPins(IReadOnlyList<PluginWidgetPin> pins)
    {
        lock (_pinsGate)
        {
            _pins = pins.ToArray();
        }
    }

    private Task<PluginWidgetPin[]> ReadPinsAsync()
    {
        lock (_pinsGate)
        {
            return Task.FromResult(_pins);
        }
    }

    private Task SetPinnedAsync(PluginWidgetPin pin, bool pinned)
    {
        return MutatePinsAsync(pins => PluginWidgetPins.Set(pins, pin, pinned));
    }

    private Task MovePinAsync(PluginWidgetPin pin, int offset)
    {
        return MutatePinsAsync(pins => PluginWidgetPins.Move(pins, pin, offset));
    }

    private Task ResetPinOrderAsync()
    {
        return MutatePinsAsync(PluginWidgetPins.ResetOrder);
    }

    private async Task MutatePinsAsync(Action<List<PluginWidgetPin>> mutate)
    {
        var pins = await Task.Run(() =>
            ConfigStore.Mutate(config => mutate(config.PluginWidgetPins)).PluginWidgetPins.ToArray());
        ApplyPins(pins);
    }
}
