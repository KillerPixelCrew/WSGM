using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Whether configuration was read, absent, corrupt or temporarily inaccessible.</summary>
public enum ConfigReadOutcome
{
    /// <summary>An existing document was loaded.</summary>
    Loaded,

    /// <summary>No document exists; defaults are available.</summary>
    Absent,

    /// <summary>The existing document could not be parsed.</summary>
    Corrupt,

    /// <summary>The file or its mutex could not be read.</summary>
    Unreadable
}

/// <summary>A read result; only Loaded and Absent carry configuration.</summary>
/// <param name="Outcome">What happened when reading.</param>
/// <param name="Config">The understood configuration, or null on failure.</param>
/// <param name="Error">The failure, when one occurred.</param>
public sealed record ConfigReadResult(ConfigReadOutcome Outcome, AppConfig? Config, Exception? Error = null)
{
    /// <summary>Requires a trustworthy configuration for mutation or recovery.</summary>
    /// <returns>The loaded document or absent-file defaults.</returns>
    /// <exception cref="ConfigUnavailableException">The existing state is unavailable.</exception>
    public AppConfig RequireConfig()
    {
        return Outcome is ConfigReadOutcome.Loaded or ConfigReadOutcome.Absent && Config is not null
            ? Config
            : throw new ConfigUnavailableException($"Configuration is {Outcome}.", Error);
    }
}

/// <summary>A strict operation could not safely obtain or publish configuration.</summary>
public sealed class ConfigUnavailableException : IOException
{
    /// <summary>Creates a failure preserving its original cause.</summary>
    /// <param name="message">What operation failed.</param>
    /// <param name="inner">The original failure.</param>
    public ConfigUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
