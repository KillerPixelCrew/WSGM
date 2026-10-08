using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Host-admitted declarative controls for one common-plugin instance.</summary>
/// <param name="Actions">Named operations with validated argument declarations.</param>
/// <param name="Contributions">Host-rendered rows referencing admitted actions and effective state.</param>
/// <param name="Widgets">Compact groups of existing contribution IDs eligible for pinning.</param>
internal sealed record PluginOverlayControls(
    IReadOnlyList<PluginAction> Actions,
    IReadOnlyList<PluginUiContribution> Contributions,
    IReadOnlyList<PluginWidget> Widgets);

/// <summary>Presentation snapshot of one plugin instance; does not transfer lifecycle ownership to the view.</summary>
/// <param name="Identity">Package and host instance identity.</param>
/// <param name="Name">Package display name.</param>
/// <param name="Generation">Current admitted generation, or zero before registration.</param>
/// <param name="Controls">Admitted control declarations, or null before they are available.</param>
/// <param name="Status">Plain lifecycle and health text.</param>
/// <param name="CanInvoke">Whether host admission currently permits actions; dispatch still revalidates.</param>
/// <param name="Error">Retained load or runtime error, or null when none is recorded.</param>
internal sealed record PluginOverlayInstance(
    PluginInstanceIdentity Identity,
    string Name,
    long Generation,
    PluginOverlayControls? Controls,
    string Status,
    bool CanInvoke,
    string? Error)
{
    /// <summary>Manifest category used for grouping; empty when no category was projected.</summary>
    internal string Category { get; init; } = string.Empty;
}

/// <summary>Host-owned persistence operations for widget pins and order.</summary>
/// <param name="Read">Returns the current pins; callers must not mutate the returned array.</param>
/// <param name="Set">Pins or unpins one widget when the boolean is true or false.</param>
/// <param name="Move">Moves one pin by a signed relative offset.</param>
/// <param name="Remove">Removes one saved pin.</param>
/// <param name="Reset">Resets saved ordering without invoking plugin actions.</param>
internal sealed record PluginWidgetPreferences(
    Func<Task<PluginWidgetPin[]>> Read,
    Func<PluginWidgetPin, bool, Task> Set,
    Func<PluginWidgetPin, int, Task> Move,
    Func<PluginWidgetPin, Task> Remove,
    Func<Task> Reset);

/// <summary>Read-only widget observations and explicit action routing, independent of package lifecycle.</summary>
internal interface ICommonPluginOverlaySource
{
    /// <summary>Reads current instance presentation without starting packages or touching hardware.</summary>
    /// <returns>A new instance array; referenced declarations remain host-owned immutable snapshots.</returns>
    PluginOverlayInstance[] Snapshot();

    /// <summary>Reads accepted effective state for one instance's current generation.</summary>
    /// <param name="identity">Exact package and instance key.</param>
    /// <returns>A new observation array; empty when the instance is absent or has published no state.</returns>
    PluginStatePublication[] State(PluginInstanceIdentity identity);

    /// <summary>Routes an explicit user action to the current owner.</summary>
    /// <param name="identity">Exact package and instance key.</param>
    /// <param name="generation">Generation shown by the snapshot; stale generations are refused.</param>
    /// <param name="action">Admitted named action ID.</param>
    /// <param name="arguments">User-entered primitive arguments; host validation supplies declared defaults.</param>
    /// <param name="cancellationToken">Cancels waiting without authorizing a retry of uncertain external work.</param>
    /// <returns>The operation-correlated result; dispatch does not imply external-state verification.</returns>
    Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken);
}

/// <summary>Routes overlay intent to the resident common host without giving views lifecycle ownership.</summary>
internal sealed class CommonPluginOverlaySource : ICommonPluginOverlaySource
{
    private readonly PluginHost _host;
    private readonly CommonPluginManager? _manager;
    private readonly object _pinsGate = new();
    private readonly ConfigStore _store;
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
            .. Device?.Snapshot() ?? []
        ];
    }

    /// <inheritdoc />
    public PluginStatePublication[] State(PluginInstanceIdentity identity)
    {
        return Device?.Snapshot().Any(instance => instance.Identity == identity) == true
            ? Device.State(identity)
            : _host.StateSnapshot(identity);
    }

    /// <inheritdoc />
    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        return Device?.Snapshot().Any(instance => instance.Identity == identity) == true
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
