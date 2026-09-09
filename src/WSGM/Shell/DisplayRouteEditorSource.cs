using System;
using System.Linq;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

internal sealed record DisplayRouteActionOption(PluginInstanceIdentity Identity, PluginAction Action, string Label)
{
    public override string ToString() => Label;
}

internal sealed record DisplayRouteEditorServices(
    Func<DisplayRouteActionOption[]> Actions,
    Func<Task<DisplayRouteConfiguration>> Read,
    Func<Task<DisplayProfile>> Capture,
    Func<bool, int, DisplayRouteBinding?, Task> Save);

internal static class DisplayRouteEditorSource
{
    internal static DisplayRouteEditorServices Create(CommonPluginOverlaySource source) => new(
        () =>
        {
            var devices = source.Device?.Snapshot().Select(instance => instance.Identity).ToArray() ?? [];
            return source.Snapshot().Where(instance => !devices.Contains(instance.Identity) && instance.Controls is not null)
                .SelectMany(instance => instance.Controls!.Actions.Select(action => new DisplayRouteActionOption(
                    instance.Identity, action, $"{instance.Name} / {instance.Identity.InstanceId}: {action.Label}"))).ToArray();
        },
        () => Task.Run(() => ConfigStore.Load().DisplayRoutes ?? new()),
        () => Task.Run(DisplayTopology.CaptureProfile),
        (enabled, index, binding) => Task.Run(() =>
        {
            if (binding is not null) { _ = DisplayRoutePlan.FromBinding(binding); }
            var saved = ConfigStore.Mutate(config =>
            {
                config.DisplayRoutes ??= new();
                config.DisplayRoutes.Enabled = enabled;
                Set(config.DisplayRoutes, index, binding);
            });
            BootManifestWriter.WriteCurrent(saved);
        }));

    internal static DisplayRouteBinding? Get(DisplayRouteConfiguration config, int index) => index switch
    {
        0 => config.EnterGameMode,
        1 => config.LeaveGameMode,
        2 => config.DesktopStartup,
        3 => config.DesktopWake,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal static void Set(DisplayRouteConfiguration config, int index, DisplayRouteBinding? binding)
    {
        switch (index)
        {
            case 0: config.EnterGameMode = binding; break;
            case 1: config.LeaveGameMode = binding; break;
            case 2: config.DesktopStartup = binding; break;
            case 3: config.DesktopWake = binding; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
