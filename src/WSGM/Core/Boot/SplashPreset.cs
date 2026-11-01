namespace WSGM.Core;

/// <summary>The built-in boot-splash presets offered by the Appearance page.</summary>
internal enum SplashPreset
{
    /// <summary>What WSGM 2.0 ships with: wordmark on near-black, accent sweep along the bottom.</summary>
    Wsgm20,

    /// <summary>The classic default look (black, white "Please wait", ring spinner).</summary>
    Classic,

    /// <summary>Large centered "WSGM" wordmark with a small ring spinner.</summary>
    Wordmark,

    /// <summary>Small wordmark inside a large accent ring on a near-black background.</summary>
    MonogramRing,

    /// <summary>Minimal dim status line near the bottom of the screen.</summary>
    QuietConsole,

    /// <summary>Wordmark with an accent sweep-line along the bottom edge.</summary>
    SweepLine
}
