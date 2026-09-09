using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record PluginOverlayControls(IReadOnlyList<PluginAction> Actions,
    IReadOnlyList<PluginUiContribution> Contributions, IReadOnlyList<PluginWidget> Widgets);

internal sealed record PluginOverlayInstance(PluginInstanceIdentity Identity, string Name, long Generation,
    PluginOverlayControls? Controls, string Status, bool CanInvoke, string? Error);

/// <summary>Read-only widget observations and explicit action routing, independent of package lifecycle.</summary>
internal interface ICommonPluginOverlaySource
{
    PluginOverlayInstance[] Snapshot();
    PluginStatePublication[] State(PluginInstanceIdentity identity);
    Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken);
}

/// <summary>Routes overlay intent to the resident common host without giving views lifecycle ownership.</summary>
internal sealed class CommonPluginOverlaySource(CommonPluginManager manager, PluginHost host) : ICommonPluginOverlaySource
{
    internal static Task SetPinnedAsync(PluginWidgetPin pin, bool pinned) => Task.Run(() =>
        ConfigStore.Mutate(config => PluginWidgetPins.Set(config.PluginWidgetPins, pin, pinned)));

    internal static Task MovePinAsync(PluginWidgetPin pin, int offset) => Task.Run(() =>
        ConfigStore.Mutate(config => PluginWidgetPins.Move(config.PluginWidgetPins, pin, offset)));

    internal static Task ResetPinOrderAsync() => Task.Run(() =>
        ConfigStore.Mutate(config => PluginWidgetPins.ResetOrder(config.PluginWidgetPins)));

    public PluginOverlayInstance[] Snapshot() => manager.Snapshot().Select(instance =>
    {
        var owner = instance.Registration;
        var actions = owner?.Actions;
        return new PluginOverlayInstance(instance.Identity, instance.Manifest.Name, owner?.Context.Generation ?? 0,
            actions is null ? null : new(actions.Actions, actions.Contributions, actions.Widgets),
            owner is null ? "Starting" : $"{owner.Health.Health}: {owner.Health.Detail}",
            owner is not null && !owner.IsStopping && !owner.Quarantined, instance.Error);
    }).ToArray();
    public PluginStatePublication[] State(PluginInstanceIdentity identity) => host.StateSnapshot(identity);
    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
        => host.InvokeActionAsync(identity, generation, action, arguments, PluginActionOrigin.User,
            DateTimeOffset.UtcNow.AddSeconds(10), cancellationToken);
}
