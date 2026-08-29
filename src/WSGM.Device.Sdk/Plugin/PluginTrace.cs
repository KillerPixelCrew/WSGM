using System;
using WSGM.Device.Sdk.Ipc;

namespace WSGM.Device.Sdk.Plugin;

/// <summary>
/// Ambient diagnostic sink for the one plugin running in a DeviceHost process.
/// </summary>
/// <remarks>
/// This is deliberately shaped like WSGM's own <c>Log</c>, static and no-op until installed, and
/// for the same reason: instrumentation that costs plumbing does not get written. The interface
/// method <see cref="IPluginHostAdapter.Trace"/> is the transport and stays the contract; this is
/// the front door for the layers that never see the adapter.
/// <para>
/// A plugin is not a library — DeviceHost hosts exactly one, for one process lifetime, so there is
/// no second sink to confuse and no ambient state shared between tenants. Tests install their own
/// sink or leave it uninstalled, in which case every call here is a branch and a return.
/// </para>
/// <para>
/// Never trace from a sample or polling loop. The controller reader runs at ~125 Hz and would
/// out-write everything else in the log, which is the failure this exists to fix, not to cause.
/// </para>
/// </remarks>
public static class PluginTrace
{
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
