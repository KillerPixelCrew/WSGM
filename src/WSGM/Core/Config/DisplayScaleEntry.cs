namespace WSGM.Core;

/// <summary>
///     One display's pre-game scaling, keyed by the GDI source device name
///     (\\.\DISPLAYn) so restore survives topology changes and later boots.
/// </summary>
public sealed class DisplayScaleEntry
{
    /// <summary>GDI source device name, such as <c>\\.\DISPLAY1</c>.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Saved scale percentage to restore for this display.</summary>
    public int Percent { get; set; }
}
