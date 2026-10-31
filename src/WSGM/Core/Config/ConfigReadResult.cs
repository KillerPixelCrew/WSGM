using System;

namespace WSGM.Core;

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
