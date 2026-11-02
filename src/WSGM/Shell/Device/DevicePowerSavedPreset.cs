using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One user-saved power preset as the preset owner projects it.</summary>
/// <param name="Preset">The stored preset.</param>
/// <param name="Fits">Whether its values fit the device's current limits; one that does not is refused on apply.</param>
internal sealed record DevicePowerSavedPreset(DevicePowerUserPreset Preset, bool Fits);
