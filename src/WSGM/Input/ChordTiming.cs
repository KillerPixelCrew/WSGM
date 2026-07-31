using System;

namespace WSGM.Input;

/// <summary>Shared chord timings, modelled on Handheld Companion's InputsManager:
/// buttons accumulate into a union that only clears on full release (so a combo does
/// not need frame-perfect presses), and a hold timer restarted on every state change
/// promotes the chord to "hold".</summary>
internal static class ChordTiming
{
    /// <summary>Time with no state change before a held chord counts as a hold.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(600);
    /// <summary>Time with no input at all before recording gives up.</summary>
    public static readonly TimeSpan RecordingExpiry = TimeSpan.FromSeconds(3);
}
