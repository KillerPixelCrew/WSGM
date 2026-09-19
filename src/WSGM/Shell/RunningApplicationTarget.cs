using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Truthful availability of the canonical running-application target.</summary>
internal enum RunningApplicationTargetState
{
    Global,
    Active,
    IdentityOnly,
    Ambiguous,
    Unavailable
}

/// <summary>
///     Canonical running-application identity shared by controller and performance policy clients.
/// </summary>
internal sealed record RunningApplicationTargetSnapshot(
    long Generation,
    long SourceGeneration,
    RunningApplicationTargetState State,
    string? ApplicationId,
    uint? SteamAppId,
    string? ExecutablePath,
    string? RtssProfileName,
    string? Diagnostic)
{
    internal static RunningApplicationTargetSnapshot Initial()
    {
        return new RunningApplicationTargetSnapshot(
            0,
            0,
            RunningApplicationTargetState.Unavailable,
            null,
            null,
            null,
            null,
            "Running-application observation has not started.");
    }
}

/// <summary>Optional executable/profile resolution for one known Steam AppID.</summary>
/// <param name="ExecutablePath">The shortcut target, when Steam exposes one.</param>
/// <param name="RtssProfileName">The executable file name RTSS keys its profile on.</param>
/// <param name="Diagnostic">Why the resolution is partial, for the log.</param>
/// <param name="InstallFolder">
///     A store title's install folder. Steam never exposes a store title's executable, so the folder is
///     what the foreground pairing is validated against: only a process running from inside it may
///     become the game's RTSS profile.
/// </param>
internal sealed record SteamRunningAppProfile(
    string? ExecutablePath,
    string? RtssProfileName,
    string? Diagnostic,
    string? InstallFolder = null);

/// <summary>
///     The application the user currently has in front of them, independent of Steam.
/// </summary>
/// <param name="ExecutableName">
///     File name of the foreground process with its extension, or <see langword="null" /> when nothing
///     usable is in front.
/// </param>
/// <param name="ExecutablePath">
///     Full image path of the same process, when it could be read. The projection needs it to prove a
///     candidate actually runs from a Steam title's install folder before pairing the two.
/// </param>
/// <param name="ProcessId">
///     The same process's identifier, or zero when it could not be read. It is what the RTSS rendering
///     proof matches on: comparing identifiers is exact, where comparing an image path against whatever
///     RTSS recorded in its own table is a guess about that table's format.
/// </param>
/// <remarks>
///     This is the second identity source, and it exists so per-application policy works outside a
///     Steam game: on the desktop, for a title launched from another launcher, or for anything the user
///     picks a profile for from the overlay. It never competes with Steam — see
///     <see cref="RunningApplicationTargetProjection" /> for the precedence rule.
/// </remarks>
internal sealed record ForegroundApplicationObservation(
    string? ExecutableName,
    string? ExecutablePath = null,
    uint ProcessId = 0)
{
    /// <summary>Nothing usable in the foreground.</summary>
    internal static ForegroundApplicationObservation None { get; } = new((string?)null);
}

/// <summary>Pure projection that never carries a previous application's identity forward.</summary>
/// <remarks>
///     Two identity sources, one answer. Steam wins whenever it names exactly one running application,
///     because that identity is the one its own launch went through and the one the shortcut's
///     executable was resolved from; the foreground window can only ever agree with it or be wrong
///     about it. The foreground fills every case where Steam names nothing — the desktop, another
///     launcher, a title started outside Steam — which is the whole reason it exists.
///     <para>
///         Deliberately not a tie-break: when Steam reports more than one running application it stays
///         ambiguous rather than letting the foreground pick a winner. The foreground says which window has
///         focus, not which of two running games the user means to configure, and quietly choosing one
///         would write a power limit against the other.
///     </para>
/// </remarks>
internal static class RunningApplicationTargetProjection
{
    /// <param name="current">The snapshot in force.</param>
    /// <param name="observation">What Steam reports.</param>
    /// <param name="profile">Steam's executable/install-folder resolution for the named AppID.</param>
    /// <param name="foreground">What the user has in front of them.</param>
    /// <param name="rendering">
    ///     The applications RTSS has hooked and is currently drawing frames for. The second, independent
    ///     proof that a foreground process is the game — see <see cref="ValidatedGameExecutable" />.
    /// </param>
    internal static RunningApplicationTargetSnapshot Apply(
        RunningApplicationTargetSnapshot current,
        SteamRunningAppsObservation observation,
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground = null,
        IReadOnlyList<RtssFrametimeSample>? rendering = null)
    {
        var candidate = Project(observation, profile);
        candidate = ApplyForeground(current, candidate, profile, foreground, rendering);
        if (Equivalent(current, candidate))
        {
            return current;
        }

        return candidate with { Generation = current.Generation + 1 };
    }

    /// <summary>
    ///     Supplies the executable profile Steam omitted without replacing Steam's canonical identity.
    /// </summary>
    private static RunningApplicationTargetSnapshot ApplyForeground(
        RunningApplicationTargetSnapshot current,
        RunningApplicationTargetSnapshot steam,
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground,
        IReadOnlyList<RtssFrametimeSample>? rendering)
    {
        if (steam.State is RunningApplicationTargetState.IdentityOnly
            && current.State is RunningApplicationTargetState.Active
            && current.SteamAppId == steam.SteamAppId
            && string.Equals(current.ApplicationId, steam.ApplicationId, StringComparison.Ordinal)
            && current.RtssProfileName is { Length: > 0 })
        {
            // Steam does not expose a launch executable for ordinary store applications. Once one
            // has been supplied, keep the pairing across focus changes — an alt-tab changes focus,
            // not the game whose per-application policy is active. A DIFFERENT executable proven to
            // run from the game's own install folder may still take the pairing over: that is a
            // launcher handing off to the real game process.
            if (ValidatedGameExecutable(profile, foreground, rendering) is { } handoff
                && !string.Equals(
                    handoff.Name,
                    current.RtssProfileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return steam with
                {
                    State = RunningApplicationTargetState.Active,
                    ExecutablePath = handoff.Path,
                    RtssProfileName = handoff.Name,
                    Diagnostic = null
                };
            }

            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ExecutablePath = current.ExecutablePath,
                RtssProfileName = current.RtssProfileName,
                Diagnostic = null
            };
        }

        if (steam.State is not (RunningApplicationTargetState.Global
                or RunningApplicationTargetState.IdentityOnly)
            || foreground?.ExecutableName is not { Length: > 0 } executable)
        {
            return steam;
        }

        // Ambiguous must stay ambiguous, and Unavailable means the Steam observation itself failed,
        // where publishing an identity would claim knowledge WSGM does not have.
        var profileName = executable.Trim();
        if (ForegroundApplicationFilter.Classify(profileName)
                is not ForegroundApplicationKind.Application
            || profileName.Length > 128
            || !profileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return steam;
        }

        if (steam.State is not RunningApplicationTargetState.IdentityOnly)
        {
            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ApplicationId = $"process:{profileName.ToLowerInvariant()}",
                SteamAppId = null,
                ExecutablePath = null,
                RtssProfileName = profileName,
                Diagnostic = null
            };
        }

        // Steam's own game. The foreground is whatever the user happens to have in focus — a
        // terminal, a browser — so a bare name must never become the game's profile: that
        // pairing is sticky for the whole run, and WindowsTerminal.exe captured HITMAN 3's
        // frame limit exactly this way (device-observed 2026-09-02). A store title's install
        // folder is known, so only an executable proven to run from it may pair. A shortcut has
        // no folder to check and its target resolution already names the executable, so its
        // rare unresolved case keeps the name-based fill.
        if (steam.SteamAppId is not { } appId
            || SteamApps.IsShortcutAppId(appId))
        {
            return steam with
            {
                State = RunningApplicationTargetState.Active,
                ExecutablePath = null,
                RtssProfileName = profileName,
                Diagnostic = null
            };
        }

        if (ValidatedGameExecutable(profile, foreground, rendering) is not { } game)
        {
            return steam;
        }

        return steam with
        {
            State = RunningApplicationTargetState.Active,
            ExecutablePath = game.Path,
            RtssProfileName = game.Name,
            Diagnostic = null
        };
    }

    /// <summary>
    ///     The foreground executable, if and only if something proves it is the game.
    /// </summary>
    /// <param name="profile">Steam's resolution for the running AppID.</param>
    /// <param name="foreground">The process the user has in front of them.</param>
    /// <param name="rendering">Applications RTSS is currently drawing frames for.</param>
    /// <returns>The proven executable, or null when nothing proves one.</returns>
    /// <remarks>
    ///     Two independent proofs, either of which is enough, because a bare foreground NAME is not one:
    ///     that pairing is sticky for the whole run, and <c>WindowsTerminal.exe</c> captured HITMAN 3's
    ///     frame limit exactly that way (Claw, 2026-09-02).
    ///     <para>
    ///         The first is Steam's own install folder — see <see cref="SteamRunningAppProfile.InstallFolder" />.
    ///         It covers every title Steam installed and is checked first because it costs nothing.
    ///     </para>
    ///     <para>
    ///         The second is RTSS: the process is one the limiter has hooked and is currently drawing frames
    ///         for. That is the only evidence available for a title Steam names but does not manage — Skyrim
    ///         SE launched through Mod Organizer reports an empty install folder, empty launch options and no
    ///         local content, so the folder proof can never be satisfied and its per-application profile
    ///         could never be written (Claw, 2026-09-04). It is also the more meaningful of the two here: an
    ///         RTSS profile for a process RTSS is not rendering would do nothing whatever, so this proof
    ///         admits exactly the processes the feature can act on. Waterfox, Mod Organizer, GameBar and
    ///         RustDesk all held focus during that run and none of them is hooked.
    ///     </para>
    ///     <para>
    ///         Matched on process id. RTSS records its own name for an entry and this must not depend on
    ///         what format that is.
    ///     </para>
    /// </remarks>
    private static (string Name, string Path)? ValidatedGameExecutable(
        SteamRunningAppProfile? profile,
        ForegroundApplicationObservation? foreground,
        IReadOnlyList<RtssFrametimeSample>? rendering)
    {
        if (foreground?.ExecutablePath is not { Length: > 0 } path)
        {
            return null;
        }

        var name = (foreground.ExecutableName ?? string.Empty).Trim();
        if (ForegroundApplicationFilter.Classify(name) is not ForegroundApplicationKind.Application
            || name.Length is 0 or > 128
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // An unparsable path proves nothing; without proof there is no pairing.
            return null;
        }

        return RunsFromInstallFolder(profile, fullPath) || IsRenderingUnderRtss(foreground, rendering)
            ? (name, fullPath)
            : null;
    }

    /// <summary>Whether the candidate runs from inside the install folder Steam named.</summary>
    private static bool RunsFromInstallFolder(SteamRunningAppProfile? profile, string fullPath)
    {
        if (profile?.InstallFolder is not { Length: > 0 } folder)
        {
            return false;
        }

        try
        {
            var fullFolder = Path.GetFullPath(folder);
            var prefix = fullFolder.EndsWith(Path.DirectorySeparatorChar)
                ? fullFolder
                : fullFolder + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether RTSS is currently drawing frames for the candidate process.</summary>
    private static bool IsRenderingUnderRtss(
        ForegroundApplicationObservation foreground,
        IReadOnlyList<RtssFrametimeSample>? rendering)
    {
        return foreground.ProcessId != 0
               && rendering is { Count: > 0 }
               && rendering.Any(sample => sample.ProcessId == foreground.ProcessId);
    }

    private static RunningApplicationTargetSnapshot Project(
        SteamRunningAppsObservation observation,
        SteamRunningAppProfile? profile)
    {
        if (!observation.Reachable)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Unavailable,
                null,
                null,
                null,
                null,
                Bound(observation.Diagnostic ?? "Steam running-app state is unavailable."));
        }

        var appIds = observation.AppIds.Distinct().Take(3).ToArray();
        if (appIds.Length == 0)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Global,
                null,
                null,
                null,
                null,
                null);
        }

        if (appIds.Length != 1)
        {
            return new RunningApplicationTargetSnapshot(
                0,
                observation.SourceGeneration,
                RunningApplicationTargetState.Ambiguous,
                null,
                null,
                null,
                null,
                "Steam reports more than one running AppID; global policy remains active.");
        }

        var appId = appIds[0];
        var profileResolved = !string.IsNullOrWhiteSpace(profile?.RtssProfileName);
        return new RunningApplicationTargetSnapshot(
            0,
            observation.SourceGeneration,
            profileResolved
                ? RunningApplicationTargetState.Active
                : RunningApplicationTargetState.IdentityOnly,
            $"steam:{appId}",
            appId,
            profile?.ExecutablePath,
            profile?.RtssProfileName,
            Bound(profile?.Diagnostic));
    }

    private static bool Equivalent(
        RunningApplicationTargetSnapshot left,
        RunningApplicationTargetSnapshot right)
    {
        return left.State == right.State
               && left.SourceGeneration == right.SourceGeneration
               && string.Equals(left.ApplicationId, right.ApplicationId, StringComparison.Ordinal)
               && left.SteamAppId == right.SteamAppId
               && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.RtssProfileName, right.RtssProfileName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Diagnostic, right.Diagnostic, StringComparison.Ordinal);
    }

    private static string? Bound(string? value)
    {
        return value is null || value.Length <= 1024
            ? value
            : value[..1024] + "...";
    }
}

/// <summary>
///     Turns what Steam reports about one running AppID into RTSS pairing evidence.
/// </summary>
/// <remarks>
///     One details read serves both kinds of entry: a shortcut names its target executable, and a
///     store title names only its install folder, because Steam never exposes a store title's
///     executable. The folder is what the foreground pairing is validated against.
/// </remarks>
internal static class SteamRunningAppPairing
{
    /// <summary>Reads and normalizes one running AppID's pairing evidence.</summary>
    /// <param name="probe">The toolkit's running-app probe, which owns the transport.</param>
    /// <param name="steamAppId">The AppID Steam named.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async Task<SteamRunningAppProfile> ResolveAsync(
        SteamRunningAppsProbe probe,
        uint steamAppId,
        CancellationToken cancellationToken)
    {
        var result = await probe.ReadDetailsAsync(steamAppId, cancellationToken).ConfigureAwait(false);
        if (result.Details is not { } details)
        {
            return new SteamRunningAppProfile(null, null, result.Error);
        }

        return SteamApps.IsShortcutAppId(steamAppId)
            ? NormalizeShortcutTarget(details.ShortcutExe)
            : NormalizeInstallFolder(details.InstallFolder);
    }

    /// <summary>Turns a store title's reported install folder into pairing evidence.</summary>
    /// <param name="folder">The <c>strInstallFolder</c> value Steam reported.</param>
    internal static SteamRunningAppProfile NormalizeInstallFolder(string folder)
    {
        folder = folder.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam did not report the running title's install folder; "
                + "RTSS remains on the global profile.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam reported an install folder that is not an absolute path.");
            }

            var normalized = Path.GetFullPath(folder);
            if (!Directory.Exists(normalized))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam's reported install folder is not present.");
            }

            return new SteamRunningAppProfile(
                null,
                null,
                "Steam exposes no executable for a store title; the RTSS profile pairs with the "
                + "foreground process running from its install folder.",
                normalized);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }
    }

    internal static SteamRunningAppProfile NormalizeShortcutTarget(string target)
    {
        target = target.Trim();
        if (target is ['"', .., '"'])
        {
            target = target[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam did not expose the running shortcut's executable.");
        }

        string profileName;
        string normalizedPath;
        try
        {
            if (!Path.IsPathFullyQualified(target))
            {
                return new SteamRunningAppProfile(
                    null,
                    null,
                    "Steam reported a shortcut target that is not an absolute path.");
            }

            normalizedPath = Path.GetFullPath(target);
            profileName = Path.GetFileName(normalizedPath);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(profileName)
            || profileName.Length > 128
            || !profileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || profileName.StartsWith("WSGM.Launch", StringComparison.OrdinalIgnoreCase))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "The shortcut target is not a truthful RTSS application profile.");
        }

        if (!File.Exists(normalizedPath))
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam's shortcut executable is no longer present.");
        }

        return new SteamRunningAppProfile(normalizedPath, profileName, null);
    }
}

/// <summary>
///     Session-owned, consumer-aware running-application monitor. It polls only the event-maintained
///     bounded snapshot while observed and publishes global/unknown immediately on exit or failure.
/// </summary>
internal interface IRunningApplicationTargetSource
{
    RunningApplicationTargetSnapshot Current { get; }
    event Action<RunningApplicationTargetSnapshot>? Changed;

    IDisposable AcquireObservation();
}

internal sealed class RunningApplicationMonitor : IRunningApplicationTargetSource, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProfileRetryInterval = TimeSpan.FromSeconds(10);
    private readonly Task _loop;
    private readonly ObservationGate _observers = new();
    private readonly SteamRunningAppsProbe _probe;
    private readonly Func<IReadOnlyList<RtssFrametimeSample>> _rendering;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _stateGate = new();
    private RunningApplicationTargetSnapshot _current;
    private bool _disposed;
    private ForegroundApplicationObservation _foreground = ForegroundApplicationObservation.None;
    private SteamRunningAppsObservation? _lastObservation;
    private DateTimeOffset _nextProfileRetry;
    private SteamRunningAppProfile? _profile;
    private uint? _profileAppId;
    private long _steamEnableGeneration;
    private volatile bool _steamEnabled;

    /// <param name="probe">The Steam running-application observer.</param>
    /// <param name="steamEnabled">Whether Steam-backed identity is switched on.</param>
    /// <param name="rendering">
    ///     The applications RTSS is currently drawing frames for, read fresh at each projection. The
    ///     second proof a foreground process is the game; null leaves only Steam's install folder.
    /// </param>
    internal RunningApplicationMonitor(
        SteamRunningAppsProbe probe,
        bool steamEnabled,
        Func<IReadOnlyList<RtssFrametimeSample>>? rendering = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _steamEnabled = steamEnabled;
        _rendering = rendering ?? (static () => []);
        _current = RunningApplicationTargetSnapshot.Initial();
        _loop = Task.Run(ObserveLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _observers.Signal();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _observers.Dispose();
        _shutdown.Dispose();
    }

    public event Action<RunningApplicationTargetSnapshot>? Changed;

    public RunningApplicationTargetSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    public IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _observers.Acquire();
    }

    /// <summary>Reports the application the user brought to the foreground.</summary>
    /// <param name="executableName">Foreground executable file name, or null for none.</param>
    /// <param name="executablePath">Full image path of the same process, when readable.</param>
    /// <param name="processId">Its process identifier, or zero when it could not be read.</param>
    /// <remarks>
    ///     Still one monitor and one projection: the foreground is an input to the same projection, not
    ///     a second observer publishing its own answer. It republishes against the last Steam
    ///     observation rather than re-reading Steam, because re-reading here would be exactly the
    ///     second CEF poll this class exists to avoid — and it would run on whatever thread the window
    ///     hook fired on.
    /// </remarks>
    internal void ReportForeground(
        string? executableName,
        string? executablePath = null,
        uint processId = 0)
    {
        SteamRunningAppsObservation? observation;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            ForegroundApplicationObservation next = new(executableName, executablePath, processId);
            if (string.Equals(
                    _foreground.ExecutableName,
                    next.ExecutableName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    _foreground.ExecutablePath,
                    next.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                && _foreground.ProcessId == next.ProcessId)
            {
                return;
            }

            _foreground = next;
            observation = _lastObservation;
        }

        if (observation is null)
        {
            // Nothing has been observed from Steam yet, so there is no snapshot to re-project
            // against. The next poll picks the foreground up from the field.
            Log.Info(
                $"Foreground application {executableName ?? "(none)"} recorded before the first "
                + "Steam observation; it applies at the next poll.");
            return;
        }

        Publish(observation, _profile);
    }

    /// <summary>Starts or stops the Steam-backed identity source without stopping foreground policy.</summary>
    /// <param name="enabled">Whether the CEF-backed source may subscribe and poll.</param>
    internal void SetSteamEnabled(bool enabled)
    {
        if (_steamEnabled == enabled || _disposed)
        {
            return;
        }

        _steamEnabled = enabled;
        Interlocked.Increment(ref _steamEnableGeneration);
        if (!enabled)
        {
            // An intentional CEF disable means Steam contributes no identity; the foreground
            // source remains authoritative for non-Steam and desktop applications.
            Publish(new SteamRunningAppsObservation(true, [], 0, null), null);
        }

        _observers.Signal();
    }

    private async Task ObserveLoopAsync()
    {
        var cancellationToken = _shutdown.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_observers.Count == 0 || !_steamEnabled)
            {
                await _observers.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            IAsyncDisposable subscription;
            try
            {
                subscription = await _probe.SubscribeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _profileAppId = null;
                _profile = null;
                _nextProfileRetry = default;
                Publish(new SteamRunningAppsObservation(false, [], 0, ex.Message), null);
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using (subscription)
            {
                while (_steamEnabled
                       && _observers.Count > 0
                       && !cancellationToken.IsCancellationRequested)
                {
                    await ObserveOnceAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ObserveOnceAsync(CancellationToken cancellationToken)
    {
        var enabledGeneration = Interlocked.Read(ref _steamEnableGeneration);
        if (!_steamEnabled)
        {
            return;
        }

        SteamRunningAppsObservation observation;
        try
        {
            observation = await _probe.ObserveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            observation = new SteamRunningAppsObservation(false, [], 0, ex.Message);
        }

        if (!_steamEnabled || enabledGeneration != Interlocked.Read(ref _steamEnableGeneration))
        {
            return;
        }

        var singleAppId = observation.Reachable ? SingleAppId(observation.AppIds) : null;
        var now = DateTimeOffset.UtcNow;
        if (ShouldResolveProfile(singleAppId, _profileAppId, _profile, now, _nextProfileRetry))
        {
            _profileAppId = singleAppId;
            _profile = singleAppId is { } value
                ? await ResolveProfileAsync(value, cancellationToken).ConfigureAwait(false)
                : null;
            if (!_steamEnabled || enabledGeneration != Interlocked.Read(ref _steamEnableGeneration))
            {
                return;
            }

            _nextProfileRetry = singleAppId is { } resolvedId
                                && ProfileUnresolved(resolvedId, _profile)
                ? now + ProfileRetryInterval
                : default;
        }

        if (_steamEnabled && enabledGeneration == Interlocked.Read(ref _steamEnableGeneration))
        {
            Publish(observation, _profile);
        }
    }

    /// <summary>The one distinct AppID Steam reports, or null for none or several.</summary>
    private static uint? SingleAppId(IReadOnlyList<uint> appIds)
    {
        uint? single = null;
        foreach (var appId in appIds)
        {
            if (single is null)
            {
                single = appId;
            }
            else if (single != appId)
            {
                return null;
            }
        }

        return single;
    }

    /// <summary>Decides when an AppID needs a fresh executable or install-folder lookup.</summary>
    internal static bool ShouldResolveProfile(
        uint? observedAppId,
        uint? resolvedAppId,
        SteamRunningAppProfile? profile,
        DateTimeOffset now,
        DateTimeOffset retryAt)
    {
        return observedAppId != resolvedAppId
               || (observedAppId is { } appId
                   && ProfileUnresolved(appId, profile)
                   && now >= retryAt);
    }

    /// <summary>Whether resolution is still missing what pairing needs for this kind of entry.</summary>
    /// <remarks>
    ///     A shortcut resolves to its target executable; a store title resolves to its install folder,
    ///     because Steam never exposes a store title's executable. Each kind retries only its own
    ///     missing answer — a resolved store title must not re-query every interval merely because its
    ///     profile name legitimately stays empty.
    /// </remarks>
    private static bool ProfileUnresolved(uint appId, SteamRunningAppProfile? profile)
    {
        return SteamApps.IsShortcutAppId(appId)
            ? string.IsNullOrWhiteSpace(profile?.RtssProfileName)
            : string.IsNullOrWhiteSpace(profile?.InstallFolder);
    }

    /// <summary>The RTSS rendering set, or none when reading it fails.</summary>
    /// <remarks>
    ///     RTSS is optional and its absence is ordinary, so a failure here costs the second proof and
    ///     nothing else — never the running-application identity itself.
    /// </remarks>
    private IReadOnlyList<RtssFrametimeSample> ReadRendering()
    {
        try
        {
            return _rendering();
        }
        catch (Exception ex)
        {
            Log.Change(
                "running-apps.rendering",
                $"RTSS rendering set unavailable for application pairing: {ex.Message}",
                LogLevel.Warn);
            return [];
        }
    }

    private void Publish(
        SteamRunningAppsObservation observation,
        SteamRunningAppProfile? profile)
    {
        RunningApplicationTargetSnapshot next;
        bool changed;
        string? foregroundName;
        // Outside the lock: this reads RTSS's shared mapping, and the state gate is held by the
        // window-hook thread as well as this one.
        var rendering = ReadRendering();
        lock (_stateGate)
        {
            _lastObservation = observation;
            foregroundName = _foreground.ExecutableName;
            next = RunningApplicationTargetProjection.Apply(
                _current,
                observation,
                profile,
                _foreground,
                rendering);
            changed = next.Generation != _current.Generation;
            _current = next;
        }

        // Keyed on content, not on state transitions: a device where Steam forever reports an
        // empty running set never transitions, and that silence is exactly the observation that
        // diagnoses "per-game controls never appear".
        Log.Change(
            "running-apps.observation",
            $"Steam running-app observation: reachable={observation.Reachable}, "
            + $"ids=[{string.Join(",", observation.AppIds)}], "
            + $"generation={observation.SourceGeneration}, "
            + $"foreground={foregroundName ?? "-"}, projected={next.State}");
        if (!changed)
        {
            return;
        }

        LogTransition(next);
        try
        {
            Changed?.Invoke(next);
        }
        catch (Exception ex)
        {
            Log.Error("Running-application target observer failed", ex);
        }
    }

    private static void LogTransition(RunningApplicationTargetSnapshot target)
    {
        switch (target.State)
        {
            case RunningApplicationTargetState.Active when target.SteamAppId is null:
                // No AppID means the foreground supplied this identity, which is worth saying
                // outright: it is the difference between "Steam launched this" and "this is simply
                // what the user has in front of them".
                Log.Info(
                    $"Foreground application is active: {target.RtssProfileName}; "
                    + "Steam reports no running application.");
                break;
            case RunningApplicationTargetState.Active:
                Log.Info(
                    $"Running application started: Steam AppID {target.SteamAppId}; "
                    + $"RTSS profile {target.RtssProfileName}.");
                break;
            case RunningApplicationTargetState.IdentityOnly:
                // The reason is the whole content of this line. Without it "executable profile
                // unavailable" is indistinguishable between Steam naming no install folder, naming
                // one that is gone, and naming one the foreground process simply is not inside —
                // three different faults with three different answers. Diagnosing which one kept
                // Skyrim's per-application profile from ever being written took a live AppDetails
                // read that this line already had the answer to (Claw, 2026-09-04).
                Log.Info(
                    $"Running application started: Steam AppID {target.SteamAppId}; "
                    + "executable profile unavailable, global RTSS policy remains active: "
                    + (target.Diagnostic ?? "no reason was recorded."));
                break;
            case RunningApplicationTargetState.Global:
                Log.Info("Running application exited; global application policy is active.");
                break;
            case RunningApplicationTargetState.Ambiguous:
                Log.Warn("Steam reports multiple running AppIDs; global application policy is active.");
                break;
            case RunningApplicationTargetState.Unavailable:
                Log.Warn(
                    $"Running-application target unavailable; global application policy is active: "
                    + $"{target.Diagnostic}");
                break;
            default:
                Log.Warn($"Running-application target entered an unknown state: {target.State}.");
                break;
        }
    }

    private async Task<SteamRunningAppProfile> ResolveProfileAsync(
        uint appId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SteamRunningAppPairing.ResolveAsync(_probe, appId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(null, null, ex.Message);
        }
    }
}
