using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Maps SDK capability display keys to the shared native and Steam UI vocabulary.</summary>
internal static class CapabilityDisplayLabels
{
    /// <summary>Resolves a declaration's standard or custom display label.</summary>
    /// <param name="display">Display metadata supplied by the capability declaration.</param>
    /// <param name="fallback">Label used for an unknown key or absent custom label.</param>
    /// <returns>The shared display text; custom text is assumed already validated by the SDK.</returns>
    internal static string For(CapabilityDisplay display, string fallback)
    {
        return display.Key switch
        {
            DisplayKey.Custom => display.CustomLabel ?? fallback,
            DisplayKey.Tdp => "TDP",
            DisplayKey.SustainedPowerLimit => "Sustained power limit",
            DisplayKey.BoostPowerLimit => "Boost power limit",
            DisplayKey.PerformanceProfile => "Performance profile",
            DisplayKey.FanMode => "Fan mode",
            DisplayKey.FanSpeed => "Fan speed",
            DisplayKey.FanCurve => "Fan curve",
            DisplayKey.FanLeft => "Left fan",
            DisplayKey.FanRight => "Right fan",
            DisplayKey.ChargeLimit => "Charge limit",
            DisplayKey.BypassCharging => "Bypass charging",
            DisplayKey.Lighting => "Lighting",
            DisplayKey.Brightness => "Brightness",
            DisplayKey.LightingEffect => "Lighting effect",
            DisplayKey.LightingEffectSpeed => "Effect speed",
            DisplayKey.CpuTemperature => "CPU temperature",
            DisplayKey.Battery => "Battery",
            DisplayKey.Controller => "Controller",
            DisplayKey.Motion => "Motion",
            DisplayKey.Rumble => "Rumble",
            DisplayKey.VariableRefreshRate => "Variable refresh rate",
            // Reached only by a key this build does not know, which means a plugin compiled against a
            // newer SDK. Every key the SDK declares belongs above so both UI surfaces stay aligned.
            _ => fallback
        };
    }
}
