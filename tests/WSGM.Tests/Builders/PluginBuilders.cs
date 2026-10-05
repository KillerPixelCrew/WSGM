using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests.Builders;

/// <summary>Plugin admission, shutdown and action steps shared by the plugin host tests.</summary>
internal static class PluginBuilders
{
    internal static Deadline Deadline => Deadline.After(TimeSpan.FromSeconds(5));

    internal static PluginRegistration Admit(
        PluginHost host,
        IPlugin plugin,
        string instance = "one",
        string? category = null)
    {
        return host.Admit(plugin, new PluginInstanceIdentity(plugin.Id, instance),
            category ?? PluginCategories.Infrared,
            PluginCategoryPolicy.Multiple, false, 1, "fixture-state");
    }

    /// <summary>A plugin built into the test process, as a common plugin manager's loader returns it.</summary>
    internal static Task<LoadedPluginPackage<IPlugin>> Loaded(IPlugin plugin)
    {
        return Task.FromResult(new LoadedPluginPackage<IPlugin>(plugin));
    }

    internal static async Task Close(PluginRegistration registration)
    {
        Assert.True(await registration.StopAsync(Deadline, CancellationToken.None));
        await registration.DisposeAsync();
    }

    internal static PluginActionStep Step(string id, int timeoutSeconds = 30)
    {
        return new PluginActionStep
        {
            Plugin = new PluginInstanceIdentity("wsgm.ir", "blaster"),
            ActionId = id,
            TimeoutSeconds = timeoutSeconds
        };
    }
}
