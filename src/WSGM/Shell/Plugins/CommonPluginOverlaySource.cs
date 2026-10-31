using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Routes overlay intent to the resident common host without giving views lifecycle ownership.</summary>
internal sealed class CommonPluginOverlaySource : ICommonPluginOverlaySource
{
    private readonly PluginHost _host;
    private readonly CommonPluginManager? _manager;
    private readonly object _pinsGate = new();
    private readonly ConfigStore _store;
    private volatile PluginInstanceIdentity[] _deviceIdentities = [];
    private PluginWidgetPin[] _pins;

    /// <summary>Connects widget presentation and explicit user intent to existing plugin/device owners.</summary>
    /// <param name="store">Borrowed persistence owner for widget pins and ordering.</param>
    /// <param name="manager">Resident package manager, or null when common packages are unavailable.</param>
    /// <param name="host">Resident action/state owner for common instances.</param>
    /// <param name="pins">Saved pin list copied into this source.</param>
    /// <param name="device">Optional adapter presenting the device through the same widget vocabulary.</param>
    internal CommonPluginOverlaySource(ConfigStore store, CommonPluginManager? manager, PluginHost host,
        IReadOnlyList<PluginWidgetPin> pins, ICommonPluginOverlaySource? device = null)
    {
        _manager = manager;
        _store = store;
        _host = host;
        Device = device;
        _pins = pins.ToArray();
        WidgetPreferences = new PluginWidgetPreferences(ReadPinsAsync, SetPinnedAsync, MovePinAsync,
            pin => SetPinnedAsync(pin, false), ResetPinOrderAsync);
    }

    /// <summary>Borrowed device widget source, or null when no device projection was supplied.</summary>
    internal ICommonPluginOverlaySource? Device { get; }

    /// <summary>Persistence callbacks for pins and order, independent of plugin hardware/action state.</summary>
    internal PluginWidgetPreferences WidgetPreferences { get; }

    /// <inheritdoc />
    public PluginOverlayInstance[] Snapshot()
    {
        // Device rows are routed by the identities this snapshot showed, so State and InvokeAsync
        // never rebuild the device projection just to answer which owner an identity belongs to.
        var device = Device?.Snapshot() ?? [];
        _deviceIdentities = [.. device.Select(instance => instance.Identity)];
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
                    owner is { IsStopping: false, Quarantined: false }, instance.Error)
                {
                    Category = instance.Manifest.Category
                };
            }),
            .. device
        ];
    }

    /// <inheritdoc />
    public PluginStatePublication[] State(PluginInstanceIdentity identity)
    {
        return Device is not null && _deviceIdentities.Contains(identity)
            ? Device.State(identity)
            : _host.StateSnapshot(identity);
    }

    /// <inheritdoc />
    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        return Device is not null && _deviceIdentities.Contains(identity)
            ? Device.InvokeAsync(identity, generation, action, arguments, cancellationToken)
            : _host.InvokeActionAsync(identity, generation, action, arguments, PluginActionOrigin.User,
                Deadline.After(TimeSpan.FromSeconds(10)), cancellationToken);
    }

    /// <summary>Replaces the local pin snapshot after configuration reload or a saved edit.</summary>
    /// <param name="pins">Complete saved list copied before publication to readers.</param>
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
            _store.Update(config =>
            {
                mutate(config.PluginWidgetPins);
                return true;
            }).PluginWidgetPins.ToArray());
        ApplyPins(pins);
    }
}
