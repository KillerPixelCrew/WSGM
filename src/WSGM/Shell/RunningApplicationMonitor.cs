using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Session-owned, consumer-aware running-application monitor. It polls only the event-maintained
///     bounded snapshot while observed and publishes global/unknown immediately on exit or failure.
/// </summary>
internal interface IRunningApplicationTargetSource
{
    /// <summary>The latest published immutable identity snapshot; reading it does not query Steam.</summary>
    RunningApplicationTargetSnapshot Current { get; }

    /// <summary>Raised on the publishing thread when projected snapshot content changes; subscribers must not block.</summary>
    event Action<RunningApplicationTargetSnapshot>? Changed;

    /// <summary>Keeps Steam observation active while at least one lease is held and Steam observation is enabled.</summary>
    /// <returns>An independent lease owned by the caller; dispose it when the consumer stops observing.</returns>
    IDisposable AcquireObservation();
}

/// <summary>Owns the shared Steam observation loop and projects foreground evidence for profile consumers.</summary>
internal sealed class RunningApplicationMonitor : IRunningApplicationTargetSource, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProfileRetryInterval = TimeSpan.FromSeconds(10);
    private readonly Func<uint, string?, bool> _foregroundExited;
    private readonly ObservationGate _observers = new();
    private readonly SteamRunningAppsProbe _probe;
    private readonly Func<IReadOnlyList<RtssFrametimeSample>> _rendering;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _stateGate = new();
    private RunningApplicationTargetSnapshot _current;
    private int _disposed;
    private ForegroundApplicationObservation _foreground = ForegroundApplicationObservation.None;
    private SteamRunningAppsObservation? _lastObservation;
    private SteamRunningAppProfile? _lastPublishedProfile;
    private Task? _loop;
    private DateTimeOffset _nextProfileRetry;
    private SteamRunningAppProfile? _profile;
    private uint? _profileAppId;

    // Non-null while Steam-backed identity is enabled. Disabling cancels it, which stops the
    // subscription, the observation and any profile read in flight; enabling installs a fresh one.
    private CancellationTokenSource? _steamScope;

    /// <param name="probe">The Steam running-application observer.</param>
    /// <param name="steamEnabled">Whether Steam-backed identity is switched on.</param>
    /// <param name="rendering">
    ///     The applications RTSS is currently drawing frames for, read fresh at each projection. The
    ///     second proof a foreground process is the game; null leaves only Steam's install folder.
    /// </param>
    /// <param name="foregroundExited">
    ///     Whether the foreground process, by identifier and image path, has exited; null asks Windows.
    /// </param>
    /// <summary>Creates an unstarted monitor over borrowed Steam and foreground observers.</summary>
    internal RunningApplicationMonitor(
        SteamRunningAppsProbe probe,
        bool steamEnabled,
        Func<IReadOnlyList<RtssFrametimeSample>>? rendering = null,
        Func<uint, string?, bool>? foregroundExited = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _steamScope = steamEnabled ? new CancellationTokenSource() : null;
        _rendering = rendering ?? (static () => []);
        _foregroundExited = foregroundExited ?? NativeShellProcess.HasExited;
        _current = RunningApplicationTargetSnapshot.Initial();
    }

    /// <summary>Cancels and joins the observation loop, then releases its owned synchronization state.</summary>
    /// <returns>Completion after the loop and its Steam subscription have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _observers.Signal();
        Task? loop;
        lock (_stateGate)
        {
            loop = _loop;
        }

        try
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        _observers.Dispose();
        Interlocked.Exchange(ref _steamScope, null)?.Dispose();
        _shutdown.Dispose();
    }

    /// <inheritdoc />
    public event Action<RunningApplicationTargetSnapshot>? Changed;

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _observers.Acquire();
    }

    /// <summary>Starts the loop once; foreground liveness continues without Steam while an observation lease is held.</summary>
    /// <exception cref="ObjectDisposedException">The monitor has been disposed.</exception>
    internal void Start()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _loop ??= Task.Run(ObserveLoopAsync);
        }
    }

    /// <summary>Reports the application the user brought to the foreground.</summary>
    /// <param name="executableName">Foreground executable file name, or null for none.</param>
    /// <param name="executablePath">Full image path of the same process, when readable.</param>
    /// <param name="processId">Its process identifier, or zero when it could not be read.</param>
    /// <remarks>
    ///     Reprojects cached Steam evidence without issuing a CEF query on the window-hook thread.
    /// </remarks>
    internal void ReportForeground(
        string? executableName,
        string? executablePath = null,
        uint processId = 0)
    {
        SteamRunningAppsObservation? observation;
        SteamRunningAppProfile? profile;
        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
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
            profile = _lastPublishedProfile;
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

        Publish(observation, profile);
    }

    /// <summary>Starts or stops the Steam-backed identity source without stopping foreground policy.</summary>
    /// <param name="enabled">Whether the CEF-backed source may subscribe and poll.</param>
    internal void SetSteamEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (enabled)
        {
            var scope = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _steamScope, scope, null) is not null)
            {
                scope.Dispose();
                return;
            }
        }
        else
        {
            if (Interlocked.Exchange(ref _steamScope, null) is not { } scope)
            {
                return;
            }

            // Not disposed: the loop may still be reading this scope's token. A source without a
            // timer owns nothing that needs releasing.
            scope.Cancel();
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
            if (_observers.Count == 0)
            {
                await _observers.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (Volatile.Read(ref _steamScope) is not { } scope)
            {
                // Foreground-only games still need liveness checks and RTSS pairing refreshes.
                // Disabling Steam must stop CEF work, not freeze the last application's identity.
                Publish(new SteamRunningAppsObservation(true, [], 0, null), null);
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var steam = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Token);
            var steamToken = steam.Token;
            IAsyncDisposable subscription;
            try
            {
                subscription = await _probe.SubscribeAsync(steamToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (steamToken.IsCancellationRequested)
            {
                // Steam was switched off while subscribing; the next pass is foreground-only.
                continue;
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
                try
                {
                    while (_observers.Count > 0 && !steamToken.IsCancellationRequested)
                    {
                        await ObserveOnceAsync(steamToken).ConfigureAwait(false);
                        await Task.Delay(PollInterval, steamToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                         && steamToken.IsCancellationRequested)
                {
                    // Steam was switched off; the next pass publishes foreground-only identity.
                }
            }
        }
    }

    private async Task ObserveOnceAsync(CancellationToken steamToken)
    {
        SteamRunningAppsObservation observation;
        try
        {
            observation = await _probe.ObserveAsync(steamToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (steamToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            observation = new SteamRunningAppsObservation(false, [], 0, ex.Message);
        }

        // A result that arrives after Steam was switched off must not replace the foreground-only
        // identity SetSteamEnabled already published.
        if (steamToken.IsCancellationRequested)
        {
            return;
        }

        var singleAppId = observation.Reachable ? SingleAppId(observation.AppIds) : null;
        var now = DateTimeOffset.UtcNow;
        if (ShouldResolveProfile(singleAppId, _profileAppId, _profile, now, _nextProfileRetry))
        {
            // Recorded only after the read completes, so a canceled read leaves no AppID paired
            // with another application's profile.
            var profile = singleAppId is { } value
                ? await ResolveProfileAsync(value, steamToken).ConfigureAwait(false)
                : null;
            _profileAppId = singleAppId;
            _profile = profile;
            _nextProfileRetry = singleAppId is { } resolvedId
                                && ProfileUnresolved(resolvedId, _profile)
                ? now + ProfileRetryInterval
                : default;
        }

        if (!steamToken.IsCancellationRequested)
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
    /// <param name="observedAppId">Single Steam AppID currently observed, or null.</param>
    /// <param name="resolvedAppId">AppID for which the cached profile was resolved.</param>
    /// <param name="profile">Cached resolution, which may be incomplete.</param>
    /// <param name="now">Current time in the retry clock.</param>
    /// <param name="retryAt">Next permitted retry time for incomplete resolution.</param>
    /// <returns>True for an identity change or an incomplete current profile whose retry time has arrived.</returns>
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
        ForegroundApplicationObservation seen;
        lock (_stateGate)
        {
            seen = _foreground;
        }

        // Steam windows preserve the last foreground candidate; discard it once its process exits.
        var exited = seen.ExecutableName is not null && _foregroundExited(seen.ProcessId, seen.ExecutablePath);
        lock (_stateGate)
        {
            if (exited && ReferenceEquals(_foreground, seen))
            {
                _foreground = ForegroundApplicationObservation.None;
                Log.Info($"Foreground application {seen.ExecutableName} exited; it no longer names the running "
                         + "application.");
            }

            _lastObservation = observation;
            _lastPublishedProfile = profile;
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
                // Preserve the resolution reason so missing folders and rejected executable pairing differ.
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
