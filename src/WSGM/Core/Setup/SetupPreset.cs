namespace WSGM.Core;

/// <summary>A named starting point on setup's profile page.</summary>
/// <param name="Features">Its feature switches.</param>
/// <param name="SteamAutostartTakeover">Whether it takes over how Steam starts.</param>
/// <param name="OtherManagersTakeover">Whether it turns off other handheld managers' autostart and services.</param>
public sealed record SetupPreset(SetupFeatures Features, bool SteamAutostartTakeover, bool OtherManagersTakeover);
