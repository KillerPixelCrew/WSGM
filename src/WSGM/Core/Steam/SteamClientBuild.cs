using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>One Steam client build: its channel and client version, with the UI's sp.js content hash.</summary>
/// <param name="Channel">
///     Steam's name for the channel, as <c>package\beta</c> holds it: <c>stable</c> when that file is
///     absent or empty, otherwise its content, such as <c>publicbeta</c>.
/// </param>
/// <param name="Version">The <c>version</c> of the channel's <c>package\steam_client_*win64.manifest</c>.</param>
/// <param name="SpContenthash">
///     The content hash Steam's UI loads <c>sp.js</c> with, which Steam's own log lines carry, or null
///     when it could not be read. Informational: builds are compared by channel and version.
/// </param>
internal sealed partial record SteamClientBuild(string Channel, string Version, string? SpContenthash)
{
    /// <summary>The channel without a <c>package\beta</c> file.</summary>
    internal const string StableChannel = "stable";

    /// <summary>The identity builds are compared and dismissed by.</summary>
    internal string Key => $"{Channel}/{Version}";

    /// <summary>Reads the installed client's build from its files, without starting or touching Steam.</summary>
    /// <param name="steamDirectory">Steam's install directory, or null when Steam is not installed.</param>
    /// <returns>The build, or null when its channel or version cannot be read.</returns>
    internal static SteamClientBuild? Read(string? steamDirectory)
    {
        if (steamDirectory is null)
        {
            return null;
        }

        try
        {
            var package = Path.Combine(steamDirectory, "package");
            var betaFile = Path.Combine(package, "beta");
            var channel = File.Exists(betaFile) ? File.ReadAllText(betaFile).Trim() : "";
            if (channel.Length == 0)
            {
                channel = StableChannel;
            }

            if (!ChannelPattern().IsMatch(channel))
            {
                return null;
            }

            var manifest = Path.Combine(package,
                channel == StableChannel ? "steam_client_win64.manifest" : $"steam_client_{channel}_win64.manifest");
            if (!File.Exists(manifest) || ManifestVersion().Match(File.ReadAllText(manifest)) is not
                    { Success: true } version)
            {
                return null;
            }

            var library = Path.Combine(steamDirectory, "steamui", "library.js");
            var spContenthash = File.Exists(library) ? ReadSpContenthash(File.ReadAllText(library)) : null;
            return new SteamClientBuild(channel, version.Groups[1].Value, spContenthash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The content hash Steam's webpack runtime requests <c>sp.js</c> with.</summary>
    /// <param name="runtime">The text of <c>steamui\library.js</c>.</param>
    /// <returns>The hash, or null when the runtime does not name sp.js.</returns>
    /// <remarks>
    ///     The runtime maps a chunk id to <c>({id:"sp",…}[id]||id)+".js?contenthash="+{id:"hash",…}[id]</c>,
    ///     so the hash is the first entry for sp's id after that literal.
    /// </remarks>
    internal static string? ReadSpContenthash(string runtime)
    {
        if (SpChunkId().Match(runtime) is not { Success: true } id)
        {
            return null;
        }

        var hashes = runtime.IndexOf(".js?contenthash=", StringComparison.Ordinal);
        if (hashes < 0)
        {
            return null;
        }

        var hash = new Regex($@"[{{,]{id.Groups[1].Value}:""([0-9a-f]{{16,}})""", RegexOptions.CultureInvariant)
            .Match(runtime, hashes);
        return hash.Success ? hash.Groups[1].Value : null;
    }

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"""version""\s+""(\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestVersion();

    [GeneratedRegex(@"[{,](\d+):""sp""", RegexOptions.CultureInvariant)]
    private static partial Regex SpChunkId();
}
