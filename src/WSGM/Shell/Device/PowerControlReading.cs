using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>What the power-preset projection reads from one power control.</summary>
/// <param name="Commandable">Whether it takes commands, which decides preset availability.</param>
/// <param name="Observed">Its observed value.</param>
internal readonly record struct PowerControlReading(
    bool Commandable,
    CapabilityValue? Observed)
{
    /// <summary>Reads one power control's commandability and observed value.</summary>
    /// <param name="view">The power control's view.</param>
    /// <returns>The reading.</returns>
    internal static PowerControlReading Of(DeviceCapabilityView view)
    {
        var state = view.Projection.State;
        return new PowerControlReading(
            CapabilityFreshness.CanCommand(state), state.ObservedValue);
    }
}
