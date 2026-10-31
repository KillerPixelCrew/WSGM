using System;
using SteamInterop;

namespace WSGM.Core;

/// <summary>One acquired Steam Input block lease over the steam-input-lease pipe client.</summary>
/// <remarks>
///     The lease is a pipe into the Steam process, the native boundary the owner and reconcile rules
///     of <see cref="SteamInputBlocker" /> sit on top of.
/// </remarks>
internal sealed class SteamInputPipeLease : IDisposable
{
    private readonly SteamInputClient _client;
    private readonly SteamInputBlockLease _lease;

    private SteamInputPipeLease(SteamInputClient client)
    {
        _client = client;
        _lease = client.Acquire();
    }

    /// <summary>Gets the gate's status when the lease was granted.</summary>
    public SteamInputStatus InitialStatus => _lease.InitialStatus;

    /// <summary>Releases the lease and lets Steam rediscover its controllers.</summary>
    /// <returns>What the release and its controller recovery did.</returns>
    public SteamInputReleaseOutcome Release()
    {
        return _lease.Release();
    }

    /// <summary>Probes the host-side controller recovery for the running Steam build.</summary>
    public void CheckHostRecovery()
    {
        _client.CheckRecovery();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lease.Dispose();
    }

    /// <summary>Creates the acquire function a process's <see cref="SteamInputBlocker" /> uses.</summary>
    /// <returns>Acquires a lease through one client created on first use.</returns>
    /// <remarks>
    ///     The ordinary WSGM UI lease keeps AllowInjection false and connects only to a resident gate.
    ///     The separate launch wrapper has its own explicit injection option. The blocker serializes
    ///     this delegate so its client is created once.
    /// </remarks>
    internal static Func<SteamInputPipeLease> Connector()
    {
        SteamInputClient? client = null;
        return () => new SteamInputPipeLease(
            client ??= new SteamInputClient(new SteamInputClientOptions { AllowInjection = false }));
    }
}
