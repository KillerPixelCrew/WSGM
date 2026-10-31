namespace WSGM.Core;

/// <summary>Severity of one log line, and the order the verbosity threshold compares against.</summary>
public enum LogLevel
{
    /// <summary>Detail worth keeping only while investigating. Suppressed unless verbose.</summary>
    Debug,

    /// <summary>A state transition or lifecycle event worth having in every log.</summary>
    Info,

    /// <summary>Something degraded, was refused, or fell back: behaviour actually changed.</summary>
    Warn,

    /// <summary>A failure the code could not handle.</summary>
    Error
}
