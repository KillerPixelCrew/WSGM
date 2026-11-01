namespace WSGM.Core;

/// <summary>The fan curve shapes offered as a starting point.</summary>
/// <remarks>
///     Three names every handheld user knows from HandheldCompanion. The shapes behind them come from a
///     <see cref="FanCurvePresetTable" />: HC's own arrays for the devices HC also supports, so a user
///     moving between the two tools gets the same fans, and the measured duties a LibHandheld definition
///     carries for a device whose fans need their own.
/// </remarks>
internal enum FanCurvePreset
{
    /// <summary>Holds a low floor and only ramps hard once the device is genuinely hot.</summary>
    Quiet,

    /// <summary>The balance of noise against temperature most users want.</summary>
    Default,

    /// <summary>Starts higher and reaches full speed sooner.</summary>
    Aggressive
}
