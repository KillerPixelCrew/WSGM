using System;
using System.Threading;
using SteamInterop;

namespace WSGM.Launch;

/// <summary>
///     Owns this wrapper's Steam Input block lease for the target's lifetime.
/// </summary>
/// <remarks>
///     The default route connects to Steam's resident shim; only the explicit injection flag permits
///     loading the gate into the running <c>steam.exe</c>. Acquisition belongs to the process Steam
///     actually launched, before the de-elevation hand-off, so the injecting route retains Steam's
///     integrity. The parent holds either kind of lease while the medium child runs the game.
/// </remarks>
internal sealed class SteamInputLeaseHost : IDisposable
{
    private readonly SteamInputClient _client;
    private SteamInputBlockLease? _lease;

    private SteamInputLeaseHost(SteamInputClient client)
    {
        _client = client;
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
                // The native release consumes the lease and closes the crash-safe
                // pipe on its failure path too, and closing that pipe is what
                // actually lifts blocking; only the recovery handshake is lost.
                LaunchLog.Error($"Steam Input lease release handshake failed: {ex.Message}");
            }
        }

        _client.Dispose();
    }

    internal static SteamInputClient CreateClient(LaunchOptions options)
    {
        var defaults = new SteamInputClientOptions();
        return new SteamInputClient(new SteamInputClientOptions
        {
            TargetName = options.TargetName ?? defaults.TargetName,
            PayloadPath = options.PayloadPath ?? defaults.PayloadPath,
            // Injection is opt-in and reachable only through --input-lease-inject.
            // Plain --input-lease connects to the shim Steam loaded itself, so the
            // wrapper cannot write into the Steam process on the default route.
            AllowInjection = options.InputLeaseInject
        });
    }

    /// <summary>Acquires a lease, or returns <see langword="null" /> if it cannot.</summary>
    /// <remarks>
    ///     Deliberately fails open: a controller that Steam keeps hold of is a
    ///     degraded experience, but a game that refuses to start is a broken one. The
    ///     failure is logged and the launch continues unblocked.
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
            if (options.InputLease)
            {
                LaunchLog.Error(
                    "Steam Input shim not resident - launching without the block. " +
                    "--input-lease uses Steam Input Management; turn it on in WSGM " +
                    "settings, or re-apply the launch fix with it off to inject instead.");
            }

            LaunchLog.Error($"Could not acquire the Steam Input block lease: {ex.Message}. " +
                            "Launching without it.");
            Console.Error.WriteLine($"Steam Input block unavailable: {ex.Message}");
            return null;
        }
    }
}
