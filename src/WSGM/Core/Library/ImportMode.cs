namespace WSGM.Core;

/// <summary>Which input route a generated entry launches with.</summary>
public enum ImportMode
{
    /// <summary>Switch the controller to Xbox 360 and inject nothing.</summary>
    ControllerOnly,

    /// <summary>Give the real game Steam's overlay and Steam Input.</summary>
    SteamIntegration
}
