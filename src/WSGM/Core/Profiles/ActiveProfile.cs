namespace WSGM.Core;

/// <summary>The application WSGM treats as running, and the game profile that matched it.</summary>
/// <param name="ApplicationId">Canonical application identity, or null when nothing is running.</param>
/// <param name="Executable">The running executable, once known.</param>
/// <param name="SteamAppId">The Steam AppID, when the application has one.</param>
/// <param name="GameProfileId">The matched game profile, enabled or not.</param>
/// <param name="Enabled">Whether the matched profile's values are in force.</param>
public sealed record ActiveProfile(
    string? ApplicationId,
    string? Executable,
    uint? SteamAppId,
    string? GameProfileId,
    bool Enabled)
{
    /// <summary>Nothing is running.</summary>
    public static ActiveProfile None { get; } = new(null, null, null, null, false);

    /// <summary>Whether an application is running at all.</summary>
    public bool HasApplication => ApplicationId is { Length: > 0 };
}
