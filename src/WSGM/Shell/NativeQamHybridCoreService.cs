using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Processor core preference for Steam's Performance dropdown. Every publication reads
/// Windows; commands validate the offered id and never retry a write.</summary>
/// <remarks>
/// The same <see cref="HybridCores"/> policy the overlay uses, so a change from either surface is
/// the same write and the next publication on the other reports it. Nothing is cached between
/// reads: activating a power scheme can carry a different preference with it, and a remembered
/// value would report a placement Windows had already replaced.
/// </remarks>
internal sealed class NativeQamHybridCoreService(HybridCores cores) : ISteamHybridCoreBackend
{
    private readonly object _sync = new();
    private string _status = string.Empty;
    private bool _requiresRead;

    internal ValueTask<SteamHybridCoreState?> ReadAsync() => new(Task.Run<SteamHybridCoreState?>(() =>
    {
        lock (_sync)
        {
            try
            {
                HybridCoreStatus status = cores.Read();
                _requiresRead = false;
                if (!status.Supported)
                {
                    // Published as unavailable rather than withheld: the row is registered for the
                    // session, and a silently absent control cannot be told from a broken one.
                    return new(false, [], string.Empty,
                        "This processor has one kind of core, so there is nothing to choose.");
                }

                return new(
                    true,
                    status.Options.Select(option =>
                        new SteamPowerProfileOption(HybridCores.IdFor(option.Mode), option.Name)).ToArray(),
                    // Empty when the machine is set to a placement WSGM does not offer. Naming one
                    // of its own modes there would claim WSGM put it there.
                    status.OnAc is { } active ? HybridCores.IdFor(active) : string.Empty,
                    string.IsNullOrEmpty(_status) ? Describe(status) : _status);
            }
            catch (Exception ex)
            {
                _requiresRead = true;
                return new(false, [], string.Empty, ex.Message);
            }
        }
    }));

    private static string Describe(HybridCoreStatus status)
    {
        string cores = $"{status.PerformanceCores} performance and {status.EfficiencyCores} efficiency cores.";
        if (status.OnAc is null || status.OnBattery is null)
        {
            return $"{cores} The current preference was not set by WSGM.";
        }
        return status.OnAc == status.OnBattery
            ? $"{cores} Applies to both battery and plugged in."
            : $"{cores} Plugged in and battery currently differ; choosing sets both.";
    }

    public Task<SteamUiCommandResult> SetHybridCoresAsync(string option, CancellationToken cancellationToken)
    {
        if (HybridCores.ModeForId(option) is not { } mode)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Unknown processor core preference."));
        }

        return Task.Run(() =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_requiresRead)
                {
                    return new SteamUiCommandResult(
                        false, "Windows state must be refreshed before another selection.");
                }

                try
                {
                    HybridCoreStatus status = cores.Read();
                    if (!status.Supported || !status.Options.Any(offered => offered.Mode == mode))
                    {
                        return new SteamUiCommandResult(
                            false, "That processor core preference is no longer offered.");
                    }

                    cores.Apply(mode, cancellationToken);
                    _status = string.Empty;
                    return new SteamUiCommandResult(true, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _requiresRead = true;
                    _status = $"Selection was not confirmed: {ex.Message}";
                    return new SteamUiCommandResult(false, _status);
                }
            }
        }, cancellationToken);
    }
}
