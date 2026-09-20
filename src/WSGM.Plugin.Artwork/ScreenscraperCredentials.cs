using System;

namespace WSGM.Plugin.Artwork;

internal static class ScreenscraperCredentials
{
#if DEBUG
    private const string DebugPasswordVariable = "WSGM_SCREENSCRAPER_DEBUG";
#endif
    private static ReadOnlySpan<byte> Key => "Nz7qL2vX9kR4mB8pW1sD6tG3"u8;
    private static readonly byte[] FoldedDevId = [0, 19, 80, 25, 56, 97, 2, 55, 75, 6, 99, 4, 93, 114];
    private static readonly byte[] FoldedDevPassword = [31, 48, 84, 55, 25, 4, 44, 25, 95, 31, 43];

    internal static string SoftName { get; } = BuildSoftName();
    internal static string DevId { get; } = Unfold(FoldedDevId);
    internal static string DevPassword { get; } = Unfold(FoldedDevPassword);
    internal static string? DebugPassword =>
#if DEBUG
        (Environment.GetEnvironmentVariable(DebugPasswordVariable) ?? "").Trim() is { Length: > 0 } value
            ? value
            : null;
#else
        null;
#endif

    private static string Unfold(byte[] folded)
    {
        var chars = new char[folded.Length];
        for (var index = 0; index < folded.Length; index++)
        {
            chars[index] = (char)(folded[index] ^ Key[index]);
        }

        return new string(chars);
    }

    private static string BuildSoftName()
    {
        var version = typeof(ScreenscraperCredentials).Assembly.GetName().Version;
        return version is null ? "WSGM-Artwork" : $"WSGM-Artwork-{version.Major}.{version.Minor}.{version.Build}";
    }
}
