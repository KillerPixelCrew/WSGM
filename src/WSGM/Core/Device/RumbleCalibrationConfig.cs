namespace WSGM.Core;

/// <summary>The user's comfort settings, separate from the device's actuator limits.</summary>
public sealed class RumbleCalibrationConfig
{
    /// <summary>Overall strength, from 0 (silent) to 100 (original strength).</summary>
    public int StrengthPercent { get; set; } = 100;

    /// <summary>Minimum nonzero bounded-pulse motor drive, from 0 to 100 percent.</summary>
    public int MinimumStrengthPercent { get; set; }

    /// <summary>Minimum bounded-pulse duration, from 0 to 500 milliseconds.</summary>
    public int MinimumPulseMilliseconds { get; set; }
}
