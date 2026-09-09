using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamInterop;

namespace WSGM.Shell;

internal enum SteamPhysicalRestoreResult
{
    Restored,
    OwnerChanged,
    Unverified,
}

/// <summary>Owns native claims around the device coordinator's physical handoff.</summary>
internal sealed class SteamControllerOwnershipAdapter : IDisposable
{
    private readonly Func<CancellationToken, Task<bool>> _releasePhysical;
    private readonly Func<CancellationToken, Task<SteamPhysicalRestoreResult>> _restorePhysical;
    private readonly ISteamControllerGate _gate;
    private readonly Func<bool> _steamAlive;
    private readonly Func<CancellationToken, Task<bool>> _ownerIsCurrent;
    private readonly Func<CancellationToken, Task<bool>> _physicalIsPresent;

    internal SteamControllerOwnershipAdapter(DeviceCoordinator device, Func<bool> steamAlive)
        : this(device.ReleaseControllerForSteamAsync, device.RestoreControllerFromSteamAsync,
            new NativeSteamControllerGate(), steamAlive, device.IsSteamControllerOwnershipCurrentAsync,
            device.IsReleasedControllerPresentAsync)
    {
    }

    internal SteamControllerOwnershipAdapter(Func<CancellationToken, Task<bool>> releasePhysical,
        Func<CancellationToken, Task<SteamPhysicalRestoreResult>> restorePhysical, ISteamControllerGate gate,
        Func<bool>? steamAlive = null, Func<CancellationToken, Task<bool>>? ownerIsCurrent = null,
        Func<CancellationToken, Task<bool>>? physicalIsPresent = null)
    {
        _releasePhysical = releasePhysical;
        _restorePhysical = restorePhysical;
        _gate = gate;
        _steamAlive = steamAlive ?? (() => true);
        _ownerIsCurrent = ownerIsCurrent ?? (_ => Task.FromResult(true));
        _physicalIsPresent = physicalIsPresent ?? (_ => Task.FromResult(true));
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
        // This loop performs presence reads only. Keep Steam access and the neutral virtual target
        // while disconnected; do not start a hardware acquisition or take a native block to poll.
        while (await _ownerIsCurrent(cancellationToken).ConfigureAwait(false)
            && !await _physicalIsPresent(cancellationToken).ConfigureAwait(false))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        if (!await _ownerIsCurrent(cancellationToken).ConfigureAwait(false))
        {
            _gate.Dispose();
            return true;
        }
        if (!_steamAlive())
        {
            // With Steam confirmed exited, no native reader remains to block. Its old pipe
            // cannot acknowledge a transition, and must not prevent physical restoration.
            _gate.Dispose();
            return await _restorePhysical(cancellationToken).ConfigureAwait(false) != SteamPhysicalRestoreResult.Unverified;
        }
        if (!_gate.BeginRestore())
        {
            return false;
        }
        SteamPhysicalRestoreResult restored = await _restorePhysical(cancellationToken).ConfigureAwait(false);
        if (restored == SteamPhysicalRestoreResult.OwnerChanged)
        {
            _gate.Dispose();
        }
        else if (restored == SteamPhysicalRestoreResult.Restored)
        {
            _gate.EndRestore();
        }
        return restored != SteamPhysicalRestoreResult.Unverified;
    }

    public void Dispose() => _gate.Dispose();

    internal bool OriginalSteamExited => _gate.OriginalSteamExited;

    internal Task<bool> OwnerIsCurrentAsync(CancellationToken cancellationToken) => _ownerIsCurrent(cancellationToken);
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
