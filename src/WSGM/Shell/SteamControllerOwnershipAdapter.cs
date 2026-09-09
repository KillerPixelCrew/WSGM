using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamInterop;

namespace WSGM.Shell;

/// <summary>Owns native claims around the device coordinator's physical handoff.</summary>
internal sealed class SteamControllerOwnershipAdapter : IDisposable
{
    private readonly Func<CancellationToken, Task<bool>> _releasePhysical;
    private readonly Func<CancellationToken, Task<bool>> _restorePhysical;
    private readonly ISteamControllerGate _gate;
    private readonly Func<bool> _steamAlive;

    internal SteamControllerOwnershipAdapter(DeviceCoordinator device, Func<bool> steamAlive)
        : this(device.ReleaseControllerForSteamAsync, device.RestoreControllerFromSteamAsync, new NativeSteamControllerGate(), steamAlive)
    {
    }

    internal SteamControllerOwnershipAdapter(Func<CancellationToken, Task<bool>> releasePhysical,
        Func<CancellationToken, Task<bool>> restorePhysical, ISteamControllerGate gate, Func<bool>? steamAlive = null)
    {
        _releasePhysical = releasePhysical;
        _restorePhysical = restorePhysical;
        _gate = gate;
        _steamAlive = steamAlive ?? (() => true);
    }

    internal async Task<bool> ReleaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gate.SupportsPassThrough
            || !await _releasePhysical(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return _gate.BeginPassThrough();
    }

    internal async Task<bool> RestoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_steamAlive())
        {
            // With Steam confirmed exited, no native reader remains to block. Its old pipe
            // cannot acknowledge a transition, and must not prevent physical restoration.
            _gate.Dispose();
            return await _restorePhysical(cancellationToken).ConfigureAwait(false);
        }
        if (!_gate.BeginRestore())
        {
            return false;
        }
        bool restored = await _restorePhysical(cancellationToken).ConfigureAwait(false);
        if (restored)
        {
            _gate.EndRestore();
        }
        return restored;
    }

    public void Dispose() => _gate.Dispose();

    internal bool OriginalSteamExited => _gate.OriginalSteamExited;
}

/// <summary>The native lease boundary used by controller ownership orchestration.</summary>
internal interface ISteamControllerGate : IDisposable
{
    bool SupportsPassThrough { get; }
    bool OriginalSteamExited { get; }
    bool BeginPassThrough();
    bool BeginRestore();
    void EndRestore();
}

internal sealed class NativeSteamControllerGate : ISteamControllerGate
{
    private SteamInputClient? _client;
    private SteamInputPassThrough? _passThrough;
    private SteamInputBlockLease? _transitionLease;
    private Process? _originalSteam;

    public bool OriginalSteamExited => _originalSteam?.HasExited == true;

    public bool SupportsPassThrough
    {
        get
        {
            _client ??= new SteamInputClient(new SteamInputClientOptions { AllowInjection = false });
            return _client.GetStatus().SupportsPassThrough;
        }
    }

    public bool BeginPassThrough()
    {
        _originalSteam?.Dispose();
        _originalSteam = FindSteamProcess()
            ?? throw new InvalidOperationException("Steam exited before pass-through acquisition.");
        // Open the handle now so later PID reuse cannot make this a different process.
        _ = _originalSteam.SafeHandle;
        // Start the rescan only after the physical controller is released and visible.
        _passThrough = (_client ?? throw new InvalidOperationException("Native support was not checked.")).AcquirePassThrough();
        return _passThrough.InitialStatus.IsPassThroughActive && !OriginalSteamExited;
    }

    public bool BeginRestore()
    {
        if (_client is null || _passThrough is null)
        {
            return false;
        }

        // A game lease may have ended during the surface interaction. Own a temporary block
        // independently so Steam cannot keep reading while physical acquisition returns to WSGM.
        _transitionLease = _client.Acquire();
        SteamInputPassThrough claim = _passThrough;
        _passThrough = null;
        SteamInputStatus status;
        if (OriginalSteamExited)
        {
            // The old pipe belongs to the exited process. The fresh block above belongs to the
            // currently running Steam, so release no longer needs a dead server's acknowledgement.
            claim.Dispose();
            status = _client.GetStatus();
        }
        else
        {
            status = claim.Release();
        }
        if (status.IsPassThroughActive)
        {
            // Another owner's override is still active. Do not race it for the physical device.
            return false;
        }
        return true;
    }

    public void EndRestore()
    {
        _transitionLease?.Dispose();
        _transitionLease = null;
    }

    public void Dispose()
    {
        _passThrough?.Dispose();
        _passThrough = null;
        _transitionLease?.Dispose();
        _transitionLease = null;
        _client?.Dispose();
        _client = null;
        _originalSteam?.Dispose();
        _originalSteam = null;
    }

    private static Process? FindSteamProcess()
    {
        using Process current = Process.GetCurrentProcess();
        Process[] processes = Process.GetProcessesByName("steam");
        Process? selected = null;
        try
        {
            Process[] candidates = processes.Where(process => process.SessionId == current.SessionId).ToArray();
            if (candidates.Length > 1)
            {
                throw new InvalidOperationException("Steam process identity is ambiguous in this session.");
            }
            selected = candidates.SingleOrDefault();
            return selected;
        }
        finally
        {
            foreach (Process process in processes)
            {
                if (!ReferenceEquals(process, selected)) { process.Dispose(); }
            }
        }
    }
}
