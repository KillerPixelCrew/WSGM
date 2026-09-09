using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit.Surfaces;

namespace WSGM.Shell;

/// <summary>The session's temporary controller ownership state.</summary>
internal enum SteamControllerOwnership
{
    Wsgm,
    Releasing,
    Steam,
    Reacquiring,
    RecoveryRequired,
}

/// <summary>Serializes a Steam interaction independently of the OEM dispatch deadline.</summary>
internal sealed class SteamControllerHandoff : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<CancellationToken, Task<bool>> _release;
    private readonly Func<CancellationToken, Task<bool>> _restore;
    private readonly Func<CancellationToken, Task<SteamSideMenuSnapshot>> _observe;
    private readonly Func<bool> _steamAlive;
    private readonly Func<bool> _originalSteamExited;
    private readonly Action<string> _trace;
    private readonly TimeProvider _time;
    private readonly TimeSpan _openTimeout;
    private Task _interaction = Task.CompletedTask;
    private SteamControllerOwnership _state;
    private bool _disposed;

    /// <summary>Creates the one session owner over physical, lease and CEF adapters.</summary>
    internal SteamControllerHandoff(
        Func<CancellationToken, Task<bool>> release,
        Func<CancellationToken, Task<bool>> restore,
        Func<CancellationToken, Task<SteamSideMenuSnapshot>> observe,
        Func<bool> steamAlive,
        Action<string> trace,
        TimeProvider? time = null,
        TimeSpan? openTimeout = null,
        Func<bool>? originalSteamExited = null)
    {
        _release = release;
        _restore = restore;
        _observe = observe;
        _steamAlive = steamAlive;
        _originalSteamExited = originalSteamExited ?? (() => false);
        _trace = trace;
        _time = time ?? TimeProvider.System;
        _openTimeout = openTimeout ?? TimeSpan.FromSeconds(5);
    }

    internal SteamControllerOwnership State
    {
        get { lock (_gate) { return _state; } }
    }

    internal Task Completion
    {
        get { lock (_gate) { return _interaction; } }
    }

    /// <summary>Chooses one observed game overlay, or the visible main window when no game overlay exists.</summary>
    internal static SteamWindowSideMenu? SelectReplayTarget(SteamSideMenuSnapshot snapshot, bool mainVisible)
    {
        if (snapshot.Windows is not { Count: > 0 } windows)
        {
            return null;
        }
        SteamWindowSideMenu[] overlays = windows.Where(window => window.ProcessId != 0).ToArray();
        return overlays.Length switch
        {
            0 => mainVisible ? windows[0] : null,
            1 => overlays[0],
            _ => null,
        };
    }

    /// <summary>Admits one semantic replay; repeated presses cannot toggle the surface again.</summary>
    internal bool TryStart(Func<CancellationToken, Task<bool>> replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        lock (_gate)
        {
            if (_disposed || _state != SteamControllerOwnership.Wsgm || !_interaction.IsCompleted)
            {
                return false;
            }

            _state = SteamControllerOwnership.Releasing;
            _interaction = Task.Run(() => RunAsync(replay));
            return true;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task<bool>> replay)
    {
        bool restorationAttempted = false;
        try
        {
            _trace("Steam handoff: releasing controller ownership.");
            if (!await _release(_shutdown.Token).ConfigureAwait(false))
            {
                SetState(SteamControllerOwnership.RecoveryRequired, "release was unverified");
                return;
            }

            SetState(SteamControllerOwnership.Steam, "physical controller released");
            bool sent = await replay(_shutdown.Token).ConfigureAwait(false);
            _trace($"Steam handoff: semantic replay {(sent ? "accepted" : "refused")}.");
            long started = _time.GetTimestamp();
            bool opened = false;
            while (!_shutdown.IsCancellationRequested && _steamAlive() && !_originalSteamExited())
            {
                SteamSideMenuSnapshot snapshot = await _observe(_shutdown.Token).ConfigureAwait(false);
                bool visible = snapshot.Windows?.Any(window =>
                    window.Menu != SteamSideMenu.None || window.OverlayActive == true) == true;
                if (visible && !opened)
                {
                    opened = true;
                    _trace("Steam handoff: native surface opened.");
                }

                // Unknown activation after a CEF reload is never evidence of closure. Once any
                // surface opened, switching between menus or overlays extends the same ownership.
                if (snapshot.AllSteamSurfacesClosed
                    && (opened || !sent || _time.GetElapsedTime(started) >= _openTimeout))
                {
                    _trace(opened ? "Steam handoff: native surfaces closed."
                        : "Steam handoff: surface did not open; closure verified.");
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), _time, _shutdown.Token).ConfigureAwait(false);
            }

            // Shutdown belongs to the session's full make-safe teardown. It must not reacquire
            // hardware while that teardown is releasing it.
            _shutdown.Token.ThrowIfCancellationRequested();
            SetState(SteamControllerOwnership.Reacquiring, "restoring controller ownership");
            restorationAttempted = true;
            bool restored = await _restore(_shutdown.Token).ConfigureAwait(false);
            SetState(restored ? SteamControllerOwnership.Wsgm : SteamControllerOwnership.RecoveryRequired,
                restored ? "controller ownership restored" : "restoration was unverified");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            SetState(SteamControllerOwnership.RecoveryRequired, "session teardown owns release");
        }
        catch (Exception ex)
        {
            // A failed observation or uncertain write does not authorize another hardware write.
            SetState(SteamControllerOwnership.RecoveryRequired,
                $"{(restorationAttempted ? "restoration" : "interaction")} failed: {ex.Message}");
        }
    }

    private void SetState(SteamControllerOwnership state, string reason)
    {
        lock (_gate) { _state = state; }
        _trace($"Steam handoff: {state}; {reason}.");
    }

    public async ValueTask DisposeAsync()
    {
        Task interaction;
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            interaction = _interaction;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await interaction.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
