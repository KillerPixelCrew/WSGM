using System;
using System.Threading;
using SteamInterop;

namespace WSGM.Launch;

/// <summary>
/// Owns this wrapper's Steam Input block lease for the target's lifetime.
/// </summary>
/// <remarks>
/// The lease injects into the running <c>steam.exe</c>, so it must be acquired by
/// the process Steam actually launched — which under WSGM's game mode is elevated.
/// A medium-integrity process cannot write into an elevated Steam, so acquisition
/// has to happen <em>before</em> the de-elevation hand-off, and the lease is held
/// by the elevated parent while the medium child runs the game.
/// </remarks>
internal sealed class SteamInputLeaseHost : IDisposable
{
    private readonly SteamInputClient _client;
    private SteamInputBlockLease? _lease;

    private SteamInputLeaseHost(SteamInputClient client) => _client = client;

    internal static SteamInputClient CreateClient(LaunchOptions options)
    {
        var defaults = new SteamInputClientOptions();
        return new SteamInputClient(new SteamInputClientOptions
        {
            TargetName = options.TargetName ?? defaults.TargetName,
            PayloadPath = options.PayloadPath ?? defaults.PayloadPath,
        });
    }

    /// <summary>Acquires a lease, or returns <see langword="null"/> if it cannot.</summary>
    /// <remarks>
    /// Deliberately fails open: a controller that Steam keeps hold of is a
    /// degraded experience, but a game that refuses to start is a broken one. The
    /// failure is logged and the launch continues unblocked.
    /// </remarks>
    internal static SteamInputLeaseHost? TryAcquire(LaunchOptions options)
    {
        SteamInputClient? client = null;
        try
        {
            client = CreateClient(options);
            var host = new SteamInputLeaseHost(client);
            host._lease = client.Acquire();
            LaunchLog.Info("Acquired Steam Input block lease for the target's lifetime.");
            Console.WriteLine("Acquiring Steam Input block lease...");
            return host;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            LaunchLog.Error($"Could not acquire the Steam Input block lease: {ex.Message}. " +
                            "Launching without it.");
            Console.Error.WriteLine($"Steam Input block unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Releases the lease and asks Steam to rediscover controllers.</summary>
    public void Dispose()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is not null)
        {
            try
            {
                var outcome = lease.Release();
                LaunchLog.Info($"Released Steam Input block lease (recovery={outcome.Recovery}).");
                if (outcome.RecoveryMessage is { Length: > 0 } message)
                {
                    LaunchLog.Error($"Steam controller recovery unavailable: {message}");
                }
                Console.WriteLine("Target exited; Steam Input unblocked.");
            }
            catch (Exception ex)
            {
                // Disposal below still closes the crash-safe pipe, which is what
                // actually lifts blocking; only the recovery handshake is lost.
                LaunchLog.Error($"Steam Input lease release handshake failed: {ex.Message}");
                lease.Dispose();
            }
        }
        _client.Dispose();
    }
}
