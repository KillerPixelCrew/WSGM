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

    /// <summary>
    ///     Detail worth recording only while investigating a specific problem, suppressed by default.
    /// </summary>
    /// <remarks>
    ///     Declared last so the numeric values of the levels that existed before it do not move. Order
    ///     here is declaration order, not severity: the host maps each level explicitly.
    /// </remarks>
    Debug
}
