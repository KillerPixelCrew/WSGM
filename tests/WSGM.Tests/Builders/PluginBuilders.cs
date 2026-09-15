using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>Plugin admission, shutdown and action steps shared by the plugin host tests.</summary>
internal static class PluginBuilders
{
    internal static DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddSeconds(5);

    internal static PluginRegistration Admit(
        PluginHost host,
        IPlugin plugin,
        string instance = "one",
        string? category = null) =>
        host.Admit(plugin, new(plugin.Id, instance), category ?? PluginCategories.Infrared,
            PluginCategoryPolicy.Multiple, false, 1, "fixture-state");

    internal static async Task Close(PluginRegistration registration)
    {
        Assert.True(await registration.StopAsync(Deadline, default));
        await registration.DisposeAsync();
    }

    internal static PluginActionStep Step(string id, int timeoutSeconds = 30) => new()
    {
        Plugin = new("wsgm.ir", "blaster"),
        ActionId = id,
        TimeoutSeconds = timeoutSeconds,
    };
}
