using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.IntelGpu;

/// <summary>The package's diagnostic lines, written into WSGM's log through the capability host.</summary>
/// <remarks>
///     A common plugin has no <see cref="PluginTrace" /> sink of its own, so every transport takes this
///     instead. Before the host is known, and in tests, the lines go nowhere. Lines are cut at
///     <see cref="PluginTrace.MaxMessageLength" />. Never throws.
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

    /// <summary>Records an exception with its type, which a bare message often leaves ambiguous.</summary>
    /// <param name="scope">Subsystem.</param>
    /// <param name="context">What was being done.</param>
    /// <param name="error">The exception.</param>
    public void Failure(string scope, string context, Exception error)
    {
        Write(DeviceTraceLevel.Error, scope, $"{context}: {Describe(error)}");
    }

    /// <summary>Records a polled state, written only when that key's value changed.</summary>
    /// <param name="level">Level for the line when it is written.</param>
    /// <param name="scope">Subsystem.</param>
    /// <param name="key">Stable identity of the thing observed.</param>
    /// <param name="message">The current state.</param>
    public void Change(DeviceTraceLevel level, string scope, string key, string message)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            _host.TraceChange(level, scope, key, Bounded(message));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostics never take a control path down with them.
        }
    }

    /// <summary>Renders an exception for a trace line: its type and message.</summary>
    /// <param name="error">The exception.</param>
    /// <returns>Plain text.</returns>
    public static string Describe(Exception error)
    {
        return $"{error.GetType().Name}: {error.Message}";
    }

    private void Write(DeviceTraceLevel level, string scope, string message)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            _host.Trace(level, scope, Bounded(message));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostics never take a control path down with them.
        }
    }

    private static string Bounded(string message)
    {
        return message.Length <= PluginTrace.MaxMessageLength ? message : message[..PluginTrace.MaxMessageLength];
    }
}
