using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WSGM.Device.Contracts.Diagnostics;

/// <summary>
/// The field vocabulary for device diagnostics, and the sanitizer that keeps identifying values out
/// of them.
/// </summary>
/// <remarks>
/// Device features are diagnosed remotely from pasted logs, so a log line has to be both useful and
/// safe to paste in public. Those two goals conflict exactly where a unique identifier would be most
/// useful, which is why sanitization happens at the point the line is built rather than being left to
/// whoever exports it.
/// </remarks>
public static class DeviceLogFields
{
    /// <summary>Package identifier.</summary>
    public const string Package = "package";

    /// <summary>Device definition identifier.</summary>
    public const string Device = "device";

    /// <summary>Host generation.</summary>
    public const string HostGeneration = "hostGen";

    /// <summary>Device generation.</summary>
    public const string DeviceGeneration = "devGen";

    /// <summary>Resource identifier.</summary>
    public const string Resource = "resource";

    /// <summary>Capability identifier.</summary>
    public const string Capability = "capability";

    /// <summary>Operation name.</summary>
    public const string Operation = "op";

    /// <summary>Elapsed milliseconds.</summary>
    public const string DurationMs = "ms";

    /// <summary>Pending items on the relevant queue.</summary>
    public const string QueueDepth = "queue";

    /// <summary>Configured timeout in milliseconds.</summary>
    public const string TimeoutMs = "timeoutMs";

    /// <summary>Outcome of the operation.</summary>
    public const string Result = "result";

    /// <summary>Structured reason code.</summary>
    public const string Reason = "reason";

    /// <summary>
    /// Builds a log line from ordered field/value pairs, sanitizing every value.
    /// </summary>
    /// <param name="operation">The operation being logged.</param>
    /// <param name="fields">Field name and value pairs, in the order they should appear.</param>
    /// <returns>A single-line, paste-safe diagnostic string.</returns>
    public static string Format(string operation, params (string Field, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        StringBuilder builder = new();
        builder.Append(Operation).Append('=').Append(Sanitize(operation));

        foreach ((string field, object? value) in fields)
        {
            builder.Append(' ').Append(field).Append('=').Append(Sanitize(value));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders a value safely for a log line.
    /// </summary>
    /// <param name="value">The value to render.</param>
    /// <returns>A single-line representation with newlines and separators neutralized.</returns>
    /// <remarks>
    /// Newlines are replaced rather than escaped: a value carrying one could otherwise forge an
    /// additional log line, and a forged line is worse than a truncated value when the log is the
    /// only diagnostic evidence available.
    /// </remarks>
    public static string Sanitize(object? value)
    {
        if (value is null)
        {
            return "-";
        }

        string text = value switch
        {
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "-",
        };

        if (text.Length == 0)
        {
            return "-";
        }

        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                '\r' or '\n' => '␀',
                ' ' => '_',
                _ when char.IsControl(c) => '␀',
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// Replaces the unique part of a device path with a stable session-local token.
    /// </summary>
    /// <param name="devicePath">A full device interface path.</param>
    /// <param name="tokens">Session-local token map, reused so one device keeps one token.</param>
    /// <returns>A path safe to log, retaining vendor and product but not the instance.</returns>
    /// <remarks>
    /// The vendor and product identifiers are kept because they are what makes a log line
    /// diagnostically useful; the instance portion is what makes it identifying. Tokens are stable
    /// within a session so two lines about the same device remain correlatable.
    /// </remarks>
    public static string TokenizeDevicePath(string devicePath, Dictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return "-";
        }

        if (tokens.TryGetValue(devicePath, out string? existing))
        {
            return existing;
        }

        // VID and PID come from the descriptor and are identical across every unit of a model, so
        // they identify the hardware rather than the owner. Everything after them - the instance
        // path, and the serial where one exists - does not.
        string prefix = "device";
        int vidIndex = devicePath.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex >= 0 && devicePath.Length >= vidIndex + 17)
        {
            prefix = devicePath.Substring(vidIndex, 17).ToUpperInvariant();
        }

        string token = $"{prefix}#{tokens.Count:D2}";
        tokens[devicePath] = token;
        return token;
    }
}
