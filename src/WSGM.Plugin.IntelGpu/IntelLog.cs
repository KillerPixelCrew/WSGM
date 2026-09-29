using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.IntelGpu;

/// <summary>The package's diagnostic lines, written into WSGM's log through the capability host.</summary>
/// <remarks>
///     A common plugin has no <see cref="PluginTrace" /> sink of its own, so every transport takes this
///     instead. Before the host is known, and in tests, the lines go nowhere. Never throws.
/// </remarks>
internal sealed class IntelLog
{
    private readonly ICapabilityHost? _host;

    /// <summary>Creates a log that writes through the host, or nowhere when it is null.</summary>
    /// <param name="host">The capability host.</param>
    public IntelLog(ICapabilityHost? host)
    {
        _host = host;
    }

    /// <summary>A log that writes nothing.</summary>
    public static IntelLog None { get; } = new(null);

    public void Info(string scope, string message)
    {
        Write(DeviceTraceLevel.Info, scope, message);
    }

    public void Warn(string scope, string message)
    {
        Write(DeviceTraceLevel.Warn, scope, message);
    }

    public void Error(string scope, string message)
    {
        Write(DeviceTraceLevel.Error, scope, message);
    }

    /// <summary>Records a polled state, written only when that key's value changed.</summary>
    /// <param name="level">Level for the line when it is written.</param>
    /// <param name="scope">Subsystem.</param>
    /// <param name="key">Stable identity of the thing observed.</param>
    /// <param name="message">The current state.</param>
    public void Change(DeviceTraceLevel level, string scope, string key, string message)
    {
        try
        {
            _host?.TraceChange(level, scope, key, message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostics never take a control path down with them.
        }
    }

    private void Write(DeviceTraceLevel level, string scope, string message)
    {
        try
        {
            _host?.Trace(level, scope, message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostics never take a control path down with them.
        }
    }
}
