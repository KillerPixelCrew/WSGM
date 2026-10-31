namespace WSGM.Core;

/// <summary>What the machine reports and what is currently in effect.</summary>
/// <param name="Supported">Whether the active scheme exposes the setting at all.</param>
/// <param name="OnAc">The mode in effect while plugged in, or null when it is not one WSGM offers.</param>
/// <param name="OnBattery">The mode in effect on battery, or null when it is not one WSGM offers.</param>
internal sealed record CpuBoostStatus(bool Supported, CpuBoostMode? OnAc, CpuBoostMode? OnBattery);
