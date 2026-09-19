using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

internal static class CapabilityDisplayLabels
{
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
