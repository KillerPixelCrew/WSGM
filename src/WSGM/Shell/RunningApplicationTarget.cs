using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    Unavailable,
}

/// <summary>
/// Canonical running-application identity shared by controller and performance policy clients.
/// </summary>
internal sealed record RunningApplicationTargetSnapshot(
    long Generation,
    long SourceGeneration,
    RunningApplicationTargetState State,
    string? ApplicationId,
    uint? SteamAppId,
    string? ExecutablePath,
    string? RtssProfileName,
    DateTimeOffset ObservedAt,
    string? Diagnostic)
{
    internal static RunningApplicationTargetSnapshot Initial(DateTimeOffset observedAt) => new(
        0,
        0,
        RunningApplicationTargetState.Unavailable,
        null,
        null,
        null,
        null,
        observedAt,
        "Running-application observation has not started.");
}

/// <summary>Bounded raw running-AppID observation from Steam.</summary>
internal sealed record SteamRunningAppObservation(
    bool Reachable,
    IReadOnlyList<uint> AppIds,
    long SourceGeneration,
    string? Diagnostic);

/// <summary>Optional executable/profile resolution for one known Steam AppID.</summary>
internal sealed record SteamRunningAppProfile(
    string? ExecutablePath,
    string? RtssProfileName,
    string? Diagnostic);

/// <summary>Read-only Steam source used by the session-owned target monitor.</summary>
internal interface IRunningApplicationProbe
{
    ValueTask<IAsyncDisposable> SubscribeAsync(CancellationToken cancellationToken);

    Task<SteamRunningAppObservation> ObserveAsync(CancellationToken cancellationToken);

    Task<SteamRunningAppProfile> ResolveProfileAsync(
        uint steamAppId,
        CancellationToken cancellationToken);
}

/// <summary>Pure projection that never carries a previous application's identity forward.</summary>
internal static class RunningApplicationTargetProjection
{
    internal static RunningApplicationTargetSnapshot Apply(
        RunningApplicationTargetSnapshot current,
        SteamRunningAppObservation observation,
        SteamRunningAppProfile? profile,
        DateTimeOffset observedAt)
    {
        RunningApplicationTargetSnapshot candidate = Project(observation, profile, observedAt);
        if (Equivalent(current, candidate))
        {
            return current with { ObservedAt = observedAt };
        }

        return candidate with { Generation = current.Generation + 1 };
    }

    private static RunningApplicationTargetSnapshot Project(
        SteamRunningAppObservation observation,
        SteamRunningAppProfile? profile,
        DateTimeOffset observedAt)
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
                observedAt,
                Bound(observation.Diagnostic ?? "Steam running-app state is unavailable."));
        }

        uint[] appIds = observation.AppIds.Distinct().Take(3).ToArray();
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
                observedAt,
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
                observedAt,
                "Steam reports more than one running AppID; global policy remains active.");
        }

        uint appId = appIds[0];
        bool profileResolved = !string.IsNullOrWhiteSpace(profile?.RtssProfileName);
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
            observedAt,
            Bound(profile?.Diagnostic));
    }

    private static bool Equivalent(
        RunningApplicationTargetSnapshot left,
        RunningApplicationTargetSnapshot right) =>
        left.State == right.State
        && left.SourceGeneration == right.SourceGeneration
        && string.Equals(left.ApplicationId, right.ApplicationId, StringComparison.Ordinal)
        && left.SteamAppId == right.SteamAppId
        && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.RtssProfileName, right.RtssProfileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Diagnostic, right.Diagnostic, StringComparison.Ordinal);

    private static string? Bound(string? value) => value is null || value.Length <= 1024
        ? value
        : value[..1024] + "...";
}

/// <summary>
/// Uses Steam's AppLifetime notification to retain a running-AppID set inside SharedJSContext.
/// The managed side only reads the bounded set and never infers application changes from focus.
/// </summary>
internal sealed class SteamRunningApplicationProbe : IRunningApplicationProbe
{
    private const uint ShortcutAppIdFloor = 0x80000000;
    private static readonly TimeSpan EvaluationBudget = TimeSpan.FromSeconds(4);

    // Steam's live UI store uses display_status 4 for the initial running set. AppLifetime
    // notifications own every transition after that seed; focused-window stores are not consulted.
    private const string ObserveExpression =
        "(()=>{try{" +
        "window.__wsgm=window.__wsgm||{};const W=window.__wsgm;" +
        "if(!W.runningAppsV1){" +
        "const ids=new Set((window.appStore&&appStore.allApps||[])" +
        ".filter(a=>Number(a.display_status)===4).map(a=>Number(a.appid))" +
        ".filter(a=>Number.isInteger(a)&&a>0&&a<=4294967295));" +
        "const R={ids:ids,gen:1,dispose:()=>{}};W.runningAppsV1=R;" +
        "const h=SteamClient.GameSessions.RegisterForAppLifetimeNotifications(e=>{" +
        "const id=Number(e&&e.unAppID);if(!Number.isInteger(id)||id<=0||id>4294967295)return;" +
        "const before=ids.size;if(e.bRunning)ids.add(id);else ids.delete(id);" +
        "if(ids.size!==before)R.gen++;});" +
        "R.dispose=()=>{try{h.unregister();}catch(_){}};}" +
        "const R=W.runningAppsV1;return JSON.stringify({ok:true,ids:[...R.ids].slice(0,3)," +
        "ambiguous:R.ids.size>1,generation:R.gen});" +
        "}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

    private const string RemoveExpression =
        "(()=>{try{const W=window.__wsgm;if(W&&W.runningAppsV1){" +
        "W.runningAppsV1.dispose();delete W.runningAppsV1;}" +
        "return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()";

    private readonly ISteamUiTransport _transport;

    internal SteamRunningApplicationProbe(ISteamUiTransport transport)
        => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<IAsyncDisposable> SubscribeAsync(CancellationToken cancellationToken)
    {
        IAsyncDisposable transportLease = await _transport.SubscribeAsync(
            SteamUiTargetRole.SharedJsContext,
            cancellationToken).ConfigureAwait(false);
        return new ProbeLease(_transport, transportLease);
    }

    public async Task<SteamRunningAppObservation> ObserveAsync(
        CancellationToken cancellationToken)
    {
        SteamUiEvaluationResult result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            ObserveExpression,
            EvaluationBudget,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamRunningAppObservation(
                false,
                [],
                0,
                result.Error ?? "Steam SharedJSContext is unavailable.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("ok", out JsonElement ok)
                || ok.ValueKind != JsonValueKind.True)
            {
                string? error = root.TryGetProperty("err", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : "Steam rejected the running-app observer.";
                return new SteamRunningAppObservation(false, [], 0, error);
            }

            List<uint> appIds = [];
            if (root.TryGetProperty("ids", out JsonElement ids)
                && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in ids.EnumerateArray().Take(3))
                {
                    if (id.TryGetUInt32(out uint appId) && appId > 0)
                    {
                        appIds.Add(appId);
                    }
                }
            }

            long sourceGeneration = root.TryGetProperty("generation", out JsonElement generation)
                && generation.TryGetInt64(out long parsedGeneration)
                ? parsedGeneration
                : 0;
            return new SteamRunningAppObservation(true, appIds, sourceGeneration, null);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppObservation(
                false,
                [],
                0,
                $"Steam running-app payload was invalid: {ex.Message}");
        }
    }

    public async Task<SteamRunningAppProfile> ResolveProfileAsync(
        uint steamAppId,
        CancellationToken cancellationToken)
    {
        if (steamAppId < ShortcutAppIdFloor)
        {
            return new SteamRunningAppProfile(
                null,
                null,
                "Steam identified the running title by AppID but did not expose its executable; "
                + "RTSS remains on the global profile.");
        }

        string expression =
            "(async()=>{try{const d=await new Promise(res=>{let t;try{" +
            "const h=SteamClient.Apps.RegisterForAppDetails(" + steamAppId + ",d=>{" +
            "clearTimeout(t);try{h.unregister();}catch(_){}res(d);});" +
            "t=setTimeout(()=>{try{h.unregister();}catch(_){}res(null);},3000);" +
            "}catch(_){res(null);}});return JSON.stringify({ok:!!d,exe:d&&d.strShortcutExe||''});" +
            "}catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        SteamUiEvaluationResult result = await _transport.EvaluateAsync(
            SteamUiTargetRole.SharedJsContext,
            expression,
            EvaluationBudget,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamRunningAppProfile(null, null, result.Error);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            string target = root.TryGetProperty("exe", out JsonElement executable)
                && executable.ValueKind == JsonValueKind.String
                ? executable.GetString() ?? string.Empty
                : string.Empty;
            return NormalizeShortcutTarget(target);
        }
        catch (Exception ex)
        {
            return new SteamRunningAppProfile(
                null,
                null,
                $"Steam shortcut target was invalid: {ex.Message}");
        }
    }

    internal static SteamRunningAppProfile NormalizeShortcutTarget(string target)
    {
        target = target.Trim();
        if (target.Length >= 2 && target[0] == '"' && target[^1] == '"')
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

    private sealed class ProbeLease(
        ISteamUiTransport transport,
        IAsyncDisposable transportLease) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await transport.EvaluateAsync(
                    SteamUiTargetRole.SharedJsContext,
                    RemoveExpression,
                    EvaluationBudget,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam running-app observer cleanup failed: {ex.Message}");
            }
            finally
            {
                await transportLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Session-owned, consumer-aware running-application monitor. It polls only the event-maintained
/// bounded snapshot while observed and publishes global/unknown immediately on exit or failure.
/// </summary>
internal sealed class RunningApplicationMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private readonly IRunningApplicationProbe _probe;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _observerSignal = new(0, 1);
    private readonly object _stateGate = new();
    private readonly Task _loop;
    private RunningApplicationTargetSnapshot _current;
    private SteamRunningAppProfile? _profile;
    private uint? _profileAppId;
    private int _observerCount;
    private bool _disposed;

    internal RunningApplicationMonitor(
        IRunningApplicationProbe probe,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _pollInterval = BoundInterval(pollInterval ?? DefaultPollInterval);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = RunningApplicationTargetSnapshot.Initial(_timeProvider.GetUtcNow());
        _loop = Task.Run(ObserveLoopAsync);
    }

    internal event Action<RunningApplicationTargetSnapshot>? Changed;

    internal RunningApplicationTargetSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    internal IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Increment(ref _observerCount) == 1)
        {
            TrySignalObserver();
        }
        return new ObservationLease(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        TrySignalObserver();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _observerSignal.Dispose();
        _shutdown.Dispose();
    }

    private async Task ObserveLoopAsync()
    {
        CancellationToken cancellationToken = _shutdown.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _observerCount) == 0)
            {
                await _observerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                Publish(new SteamRunningAppObservation(false, [], 0, ex.Message), null);
                await Task.Delay(_pollInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            await using (subscription)
            {
                while (Volatile.Read(ref _observerCount) > 0
                    && !cancellationToken.IsCancellationRequested)
                {
                    await ObserveOnceAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(_pollInterval, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ObserveOnceAsync(CancellationToken cancellationToken)
    {
        SteamRunningAppObservation observation;
        try
        {
            observation = await _probe.ObserveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            observation = new SteamRunningAppObservation(false, [], 0, ex.Message);
        }

        uint? singleAppId = observation.Reachable && observation.AppIds.Distinct().Take(2).ToArray()
            is [uint appId]
            ? appId
            : null;
        if (singleAppId != _profileAppId)
        {
            _profileAppId = singleAppId;
            _profile = singleAppId is { } value
                ? await ResolveProfileAsync(value, cancellationToken).ConfigureAwait(false)
                : null;
        }

        Publish(observation, _profile);
    }

    private void Publish(
        SteamRunningAppObservation observation,
        SteamRunningAppProfile? profile)
    {
        RunningApplicationTargetSnapshot next;
        bool changed;
        lock (_stateGate)
        {
            next = RunningApplicationTargetProjection.Apply(
                _current,
                observation,
                profile,
                _timeProvider.GetUtcNow());
            changed = next.Generation != _current.Generation;
            _current = next;
        }

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
            case RunningApplicationTargetState.Active:
                Log.Info(
                    $"Running application started: Steam AppID {target.SteamAppId}; "
                    + $"RTSS profile {target.RtssProfileName}.");
                break;
            case RunningApplicationTargetState.IdentityOnly:
                Log.Info(
                    $"Running application started: Steam AppID {target.SteamAppId}; "
                    + "executable profile unavailable, global RTSS policy remains active.");
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
        }
    }

    private async Task<SteamRunningAppProfile> ResolveProfileAsync(
        uint appId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ResolveProfileAsync(appId, cancellationToken).ConfigureAwait(false);
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

    private void ReleaseObservation()
    {
        int remaining = Interlocked.Decrement(ref _observerCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _observerCount, 0);
        }
    }

    private void TrySignalObserver()
    {
        try
        {
            if (_observerSignal.CurrentCount == 0)
            {
                _observerSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static TimeSpan BoundInterval(TimeSpan interval) => interval < TimeSpan.FromMilliseconds(250)
        ? TimeSpan.FromMilliseconds(250)
        : interval > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : interval;

    private sealed class ObservationLease(RunningApplicationMonitor owner) : IDisposable
    {
        private RunningApplicationMonitor? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseObservation();
    }
}
