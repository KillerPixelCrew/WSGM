using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Ir.Tests.Builders;

/// <summary>Builds the contexts, configurations and action requests the IR plugin tests send.</summary>
internal static class IrActions
{
    internal static PluginContext Context(string folder)
    {
        return new PluginContext(new PluginInstanceIdentity("wsgm.ir", "test"), 1, PluginSessionMode.Desktop,
            DateTimeOffset.UtcNow.AddMinutes(1), folder);
    }

    internal static PluginConfiguration Configuration(string port = "", string transport = "usb", string host = "")
    {
        return new PluginConfiguration(1, PluginConfigurationOrigin.User, new Dictionary<string, PluginValue>
        {
            ["port"] = new(Text: port),
            ["transport"] = new(Text: transport),
            ["host"] = new(Text: host)
        });
    }

    /// <summary>The action's declared defaults with the given arguments replaced.</summary>
    internal static Dictionary<string, PluginValue> Arguments(IrPlugin plugin, string action,
        params (string Key, PluginValue Value)[] changes)
    {
        var arguments = plugin.Actions.Single(item => item.Id == action)
            .Arguments.ToDictionary(item => item.Key, item => item.Default);
        foreach (var change in changes)
        {
            arguments[change.Key] = change.Value;
        }

        return arguments;
    }

    /// <summary>Runs an action the way the user starts one.</summary>
    internal static ValueTask<PluginActionResult> Invoke(IrPlugin plugin, PluginContext context, string action,
        params (string Key, PluginValue Value)[] changes)
    {
        return Run(plugin, context, PluginActionOrigin.User, action, changes);
    }

    /// <summary>Runs an action the way a session automation step starts one.</summary>
    internal static ValueTask<PluginActionResult> InvokeAutomated(IrPlugin plugin, PluginContext context, string action,
        params (string Key, PluginValue Value)[] changes)
    {
        return Run(plugin, context, PluginActionOrigin.SessionAutomation, action, changes);
    }

    private static ValueTask<PluginActionResult> Run(IrPlugin plugin, PluginContext context, PluginActionOrigin origin,
        string action, (string Key, PluginValue Value)[] changes)
    {
        return plugin.ExecuteActionAsync(
            new PluginActionRequest(Guid.NewGuid(), action, origin, Arguments(plugin, action, changes)), context,
            CancellationToken.None);
    }
}
