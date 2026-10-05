using WSGM.Core;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Shell;

/// <summary>Writes plugin diagnostics into WSGM's log in one shape for the device package and common plugins.</summary>
/// <remarks>
///     Each owner checks that its plugin is still current before it calls here. Lines read
///     <c>&lt;prefix&gt;/&lt;scope&gt;: &lt;message&gt;</c>; the device package's prefix is <c>plugin</c> and a common
///     plugin's is <c>plugin/&lt;pluginId&gt;</c>.
/// </remarks>
internal static class PluginLogLine
{
    /// <summary>Writes one line. An empty message writes nothing.</summary>
    /// <param name="prefix">The owner's prefix.</param>
    /// <param name="level">How much the line matters.</param>
    /// <param name="scope">The plugin subsystem; blank means <c>plugin</c>.</param>
    /// <param name="message">The line.</param>
    internal static void Write(string prefix, DeviceTraceLevel level, string scope, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var line = Line(prefix, scope, message);
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (level)
        {
            case DeviceTraceLevel.Warn:
                Log.Warn(line);
                break;
            case DeviceTraceLevel.Error:
                Log.Error(line);
                break;
            case DeviceTraceLevel.Debug:
                Log.Debug(line);
                break;
            default:
                Log.Info(line);
                break;
        }
    }

    /// <summary>Writes a polled state only when that key's value changed.</summary>
    /// <param name="prefix">The owner's prefix.</param>
    /// <param name="level">Level for the line when it is written.</param>
    /// <param name="scope">The plugin subsystem; blank means <c>plugin</c>.</param>
    /// <param name="key">The observed thing within the scope; blank means <c>state</c>.</param>
    /// <param name="message">The current state.</param>
    /// <remarks>The key is namespaced by scope so two subsystems cannot collide on a short name like "state".</remarks>
    internal static void WriteChange(string prefix, DeviceTraceLevel level, string scope, string key, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        Log.Change(
            $"{prefix}/{Scope(scope)}/{(string.IsNullOrWhiteSpace(key) ? "state" : key)}",
            Line(prefix, scope, message),
            level switch
            {
                DeviceTraceLevel.Warn => LogLevel.Warn,
                DeviceTraceLevel.Error => LogLevel.Error,
                DeviceTraceLevel.Debug => LogLevel.Debug,
                _ => LogLevel.Info
            });
    }

    private static string Line(string prefix, string scope, string message)
    {
        return $"{prefix}/{Scope(scope)}: {message}";
    }

    private static string Scope(string scope)
    {
        return string.IsNullOrWhiteSpace(scope) ? "plugin" : scope;
    }
}
