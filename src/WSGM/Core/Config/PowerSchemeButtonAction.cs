namespace WSGM.Core;

/// <summary>
///     One power plan's Windows power-button action (PBUTTONACTION) as it was before WSGM took the
///     button over.
/// </summary>
public sealed class PowerSchemeButtonAction
{
    /// <summary>Power-plan GUID without surrounding braces.</summary>
    public string SchemeGuid { get; set; } = "";

    /// <summary>Saved action while plugged in (0 do nothing, 1 sleep, 2 hibernate, 3 shut down, 4 display off).</summary>
    public uint AcValue { get; set; }

    /// <summary>Saved action on battery, with the same values as <see cref="AcValue" />.</summary>
    public uint DcValue { get; set; }
}
