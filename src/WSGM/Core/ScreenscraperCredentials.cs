using System;

namespace WSGM.Core;

/// <summary>
/// The Screenscraper.fr developer credentials WSGM ships, and the user overrides that replace them.
/// </summary>
/// <remarks>
/// Screenscraper issues developer credentials per application rather than per user, so a build that
/// shipped without them offered an artwork source nobody could turn on: registering an application
/// with Screenscraper is not something a player does to look up a box cover. WSGM therefore carries
/// its own registered pair, which is what every other frontend talking to this API does.
/// <para>
/// The bytes below are folded against <see cref="Key"/> only to keep the pair out of plain string
/// scans of the repository and of the shipped binary. That is obfuscation, not secrecy: the key sits
/// a few lines away and anyone who wants the credentials can have them in a minute. Nothing here is
/// treated as a secret, which is also why the pair is not injected from a build secret — a public
/// installer hands the credentials to anyone who unpacks it either way, so the only thing a
/// build-time secret would buy is local developer builds silently losing the feature.
/// </para>
/// <para>
/// The quota attached to the shipped pair is shared by every WSGM install, so
/// <see cref="AppConfig.ScreenscraperDevId"/> stays available for anyone who would rather spend
/// their own. A personal account (<see cref="AppConfig.ScreenscraperUser"/>) is the lighter answer
/// and raises the quota without any developer registration at all.
/// </para>
/// </remarks>
public static class ScreenscraperCredentials
{
    /// <summary>Environment variable carrying the developer debug password, for local builds.</summary>
    public const string DebugPasswordVariable = "WSGM_SCREENSCRAPER_DEBUG";

    private static readonly byte[] Key =
    [
        78, 122, 55, 113, 76, 50, 118, 88, 57, 107, 82, 52,
        109, 66, 56, 112, 87, 49, 115, 68, 54, 116, 71, 51,
    ];

    private static readonly byte[] FoldedDevId =
        [0, 19, 80, 25, 56, 97, 2, 55, 75, 6, 99, 4, 93, 114];

    private static readonly byte[] FoldedDevPassword =
        [31, 48, 84, 55, 25, 4, 44, 25, 95, 31, 43];

    /// <summary>How WSGM names itself to Screenscraper, as the API's <c>softname</c>.</summary>
    /// <remarks>
    /// Screenscraper echoes the softname back inside the media URLs it returns, so a space in it
    /// arrives inside URLs that then have to be repaired. A dash keeps the version readable and
    /// leaves the returned URLs usable as they stand.
    /// </remarks>
    public static string SoftName { get; } = BuildSoftName();

    /// <summary>
    /// The developer debug password for this build, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Screenscraper's debug mode forces cache refreshes and fakes quota counters, and is capped at
    /// 100 uses a day against the developer account as a whole. It is a maintainer tool for
    /// exercising the quota paths, not a user setting, so it is compiled out of Release entirely and
    /// read from the environment rather than from configuration even in Debug.
    /// </remarks>
    public static string? DebugPassword =>
#if DEBUG
        (Environment.GetEnvironmentVariable(DebugPasswordVariable) ?? "").Trim() is { Length: > 0 } value
            ? value
            : null;
#else
        null;
#endif

    /// <summary>Resolves the developer credentials a request should carry.</summary>
    /// <param name="config">The configuration whose overrides are consulted.</param>
    /// <returns>The user's own pair when they supplied one, otherwise the pair WSGM ships.</returns>
    /// <remarks>
    /// Both or neither. A half-filled override would otherwise pair the user's id with WSGM's
    /// password, and Screenscraper would answer with a credential rejection they had no way to
    /// explain from what they had typed.
    /// </remarks>
    public static (string Id, string Password) Resolve(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string id = (config.ScreenscraperDevId ?? "").Trim();
        string password = (config.ScreenscraperDevPassword ?? "").Trim();
        return id.Length > 0 && password.Length > 0
            ? (id, password)
            : (Unfold(FoldedDevId), Unfold(FoldedDevPassword));
    }

    private static string Unfold(byte[] folded)
    {
        char[] chars = new char[folded.Length];
        for (int i = 0; i < folded.Length; i++)
        {
            chars[i] = (char)(folded[i] ^ Key[i]);
        }
        return new string(chars);
    }

    private static string BuildSoftName()
    {
        Version? version = typeof(ScreenscraperCredentials).Assembly.GetName().Version;
        return version is null ? "WSGM" : $"WSGM-{version.Major}.{version.Minor}.{version.Build}";
    }
}
