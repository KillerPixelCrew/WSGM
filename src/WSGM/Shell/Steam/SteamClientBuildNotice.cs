using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Tells the user, once per build, that Steam updated to a client build this WSGM was not checked with.
/// </summary>
/// <remarks>
///     Information only: nothing is gated, disabled or degraded on it. The running client's build is read
///     from Steam's files once Steam's UI is reachable, so a Steam that updated itself while starting is
///     read after the update. An unchecked build is logged once per session; its notice shows in the
///     Overlay and on WSGM's Steam settings page until the user dismisses that build.
/// </remarks>
internal sealed class SteamClientBuildNotice
{
    /// <summary>Where users report a Steam UI problem.</summary>
    internal const string ReportUrl = "https://github.com/KillerPixelCrew/WSGM/issues";

    private readonly Func<Action<AppConfig>, bool, AppConfig>? _commit;
    private readonly Func<AppConfig> _config;
    private readonly Lock _gate = new();
    private readonly IReadOnlyList<SteamClientBuild> _known;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly Func<SteamClientBuild?> _read;
    private SteamClientBuild? _shown;
    private bool _unreadableLogged;

    /// <summary>Creates the notice.</summary>
    /// <param name="read">Reads the installed client's build, or null when it cannot be read.</param>
    /// <param name="known">The builds this WSGM was checked with.</param>
    /// <param name="config">The configuration the session runs on, for the dismissed build.</param>
    /// <param name="commit">Persists a dismissal, or null where nothing may be written.</param>
    internal SteamClientBuildNotice(Func<SteamClientBuild?> read, IReadOnlyList<SteamClientBuild> known,
        Func<AppConfig> config, Func<Action<AppConfig>, bool, AppConfig>? commit)
    {
        _read = read;
        _known = known;
        _config = config;
        _commit = commit;
    }

    /// <summary>The notice to show now, or null when there is none.</summary>
    internal string? Text
    {
        get
        {
            lock (_gate)
            {
                return _shown is { } build ? Describe(build) : null;
            }
        }
    }

    /// <summary>Raised, on the calling thread, when <see cref="Text" /> changed.</summary>
    internal event Action? Changed;

    /// <summary>The notice for an unchecked build.</summary>
    /// <param name="build">The build.</param>
    /// <returns>The user-facing text.</returns>
    internal static string Describe(SteamClientBuild build)
    {
        return $"Steam updated to a build this version of WSGM hasn't been checked with ({ChannelName(build.Channel)} "
               + $"{build.Version}). If anything in Steam misbehaves, please report it at {ReportUrl} with your log: "
               + "WSGM Settings > System > Upload log gives you a link to paste.";
    }

    /// <summary>Reads the running client's build and compares it with the checked builds.</summary>
    /// <returns>The comparison.</returns>
    /// <remarks>Reads Steam's files; call it off the UI thread.</remarks>
    internal SteamClientBuildStatus Evaluate()
    {
        var build = _read();
        var status = SteamClientBuilds.Classify(build, _known);
        var changed = false;
        lock (_gate)
        {
            switch (status)
            {
                case SteamClientBuildStatus.Unreadable:
                    if (!_unreadableLogged)
                    {
                        _unreadableLogged = true;
                        Log.Info("Steam client build could not be read from Steam's package files; "
                                 + "no build check this session.");
                    }

                    break;
                case SteamClientBuildStatus.Checked:
                    if (_logged.Add(build!.Key))
                    {
                        Log.Info($"Steam client build {build.Version} ({build.Channel}, sp.js "
                                 + $"{build.SpContenthash ?? "unknown"}) is one this WSGM version was checked with.");
                    }

                    break;
                case SteamClientBuildStatus.Unchecked:
                    if (_logged.Add(build!.Key))
                    {
                        Log.Warn($"Steam client build {build.Version} ({build.Channel}, sp.js "
                                 + $"{build.SpContenthash ?? "unknown"}) hasn't been checked with this WSGM version. "
                                 + "Steam UI features stay on; report anything that misbehaves.");
                    }

                    break;
            }

            var show = status == SteamClientBuildStatus.Unchecked
                       && !string.Equals(_config().DismissedSteamClientBuild, build!.Key, StringComparison.Ordinal)
                ? build
                : null;
            if (_shown != show)
            {
                _shown = show;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return status;
    }

    /// <summary>Dismisses the notice for the build it names, so it shows again only for a later build.</summary>
    /// <returns>Whether a notice was dismissed.</returns>
    internal bool Dismiss()
    {
        SteamClientBuild? build;
        lock (_gate)
        {
            build = _shown;
            _shown = null;
        }

        if (build is null)
        {
            return false;
        }

        try
        {
            _commit?.Invoke(config => config.DismissedSteamClientBuild = build.Key, false);
        }
        catch (Exception error) when (error is ConfigUnavailableException or IOException
                                          or UnauthorizedAccessException)
        {
            // The notice stays hidden for this session; it returns at the next start.
            Log.Warn($"The Steam client build notice could not be saved as dismissed: {error.Message}");
        }

        Changed?.Invoke();
        return true;
    }

    private static string ChannelName(string channel)
    {
        return channel switch
        {
            SteamClientBuild.StableChannel => "Stable",
            "publicbeta" => "Beta",
            _ => channel
        };
    }
}
