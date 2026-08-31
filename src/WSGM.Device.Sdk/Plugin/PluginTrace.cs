using System;

namespace WSGM.Device.Sdk.Plugin;

/// <summary>Severity of a plugin diagnostic.</summary>
public enum DeviceTraceLevel
{
    /// <summary>A normal decision, observation, or state change.</summary>
    Info,

    /// <summary>A degraded, refused, or fallback path.</summary>
    Warn,

    /// <summary>A failure the plugin could not handle.</summary>
    Error,
}

/// <summary>
/// Ambient diagnostic sink for the active device plugin.
/// </summary>
/// <remarks>
/// This is deliberately shaped like WSGM's own <c>Log</c>, static and no-op until installed, and
/// for the same reason: instrumentation that costs plumbing does not get written. Tests may install
/// a sink or leave it unset. Sample loops must not trace because controller input runs at ~125 Hz.
/// </remarks>
public static class PluginTrace
{
    /// <summary>Longest plugin diagnostic WSGM records.</summary>
    public const int MaxMessageLength = 1024;

    private static IPluginHostAdapter? _sink;

    /// <summary>Routes subsequent trace calls to a host adapter.</summary>
    /// <param name="sink">The adapter to write through, or null to silence tracing.</param>
    /// <remarks>
    /// Called by the plugin once it has its host adapter, normally as the first statement of
    /// <c>StartAsync</c>. Installing it early is the point: the failures worth tracing are the ones
    /// that happen during startup.
    /// </remarks>
    public static void Install(IPluginHostAdapter? sink) => _sink = sink;

    /// <summary>Records a decision, observation, or state change on a normal path.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="message">The line.</param>
    public static void Info(string scope, string message) =>
        Write(DeviceTraceLevel.Info, scope, message);

    /// <summary>Records something that degraded, was refused, or fell back.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="message">The line.</param>
    public static void Warn(string scope, string message) =>
        Write(DeviceTraceLevel.Warn, scope, message);

    /// <summary>Records a failure the plugin could not handle.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="message">The line.</param>
    public static void Error(string scope, string message) =>
        Write(DeviceTraceLevel.Error, scope, message);

    /// <summary>Records a caught exception with its type and message.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="context">What was being attempted.</param>
    /// <param name="ex">The exception that ended it.</param>
    /// <remarks>
    /// A <c>catch</c> that sets a flag and moves on is the single most common way this codebase
    /// lost a diagnosis: the WMI provider probe turned every failure into
    /// <c>providerAvailable = false</c>, so a permissions problem, a missing instance and a
    /// malformed response all reached the user as the same partially-available device. One call
    /// here at the top of such a block is the difference.
    /// </remarks>
    public static void Failure(string scope, string context, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Write(DeviceTraceLevel.Warn, scope, $"{context}: {ex.GetType().Name}: {ex.Message}");
    }

    private static void Write(DeviceTraceLevel level, string scope, string message)
    {
        IPluginHostAdapter? sink = _sink;
        if (sink is null || string.IsNullOrEmpty(message))
        {
            return;
        }

        try
        {
            sink.Trace(level, scope, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A trace must never be able to fail the operation it was describing, and the sink is
            // an interface a plugin's own test double can implement badly.
        }
    }
}
