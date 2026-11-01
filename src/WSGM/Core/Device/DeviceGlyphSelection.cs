namespace WSGM.Core;

/// <summary>How WSGM chooses a device glyph profile.</summary>
public enum DeviceGlyphSelection
{
    /// <summary>Use only the exact verified profile advertised by the device definition.</summary>
    Automatic,

    /// <summary>Leave Steam's own glyphs untouched.</summary>
    NativeSteam,

    /// <summary>Use an explicitly selected reviewed catalog profile.</summary>
    ManualReviewedProfile
}
