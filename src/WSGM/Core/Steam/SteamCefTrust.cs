namespace WSGM.Core;

/// <summary>Shared consent text describing the authority granted to common-plugin Steam frontends.</summary>
internal static class SteamCefTrust
{
    /// <summary>Trust disclosure shown before enabling unrestricted plugin JavaScript/CSS in Steam.</summary>
    internal const string Warning =
        "Steam CEF plugins run without a sandbox. They can change Steam's UI, access your Steam session and account data, and communicate with their WSGM backend. Install only plugins you trust. A repository signature proves origin and integrity, not review or safety.";
}
