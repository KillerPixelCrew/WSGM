using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Processor core preference for Steam's Performance dropdown. Commands validate published
///     options and publish accepted writes; independent refreshes read Windows.
/// </summary>
/// <remarks>
///     The same <see cref="HybridCores" /> policy the overlay uses, so a change from either surface is
///     the same write. The first publication after a selection carries the written value without
///     a confirming read. Later refreshes observe scheme changes. Failed writes never close admission
///     for another explicit selection.
/// </remarks>
/// <param name="cores">
///     Borrowed Windows core-placement owner shared with the overlay; this adapter serializes its own
///     reads and writes.
/// </param>
internal sealed class NativeQamHybridCoreService(HybridCores cores) : ISteamHybridCoreBackend
{
    private readonly Lock _sync = new();
    private bool _publishWritten;
    private HybridCoreStatus? _published;
    private string _status = string.Empty;

    /// <inheritdoc />
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
                try
                {
                    var status = _published;
                    if (status is not { Supported: true } || status.Options.All(offered => offered.Mode != mode))
                    {
                        return new SteamUiCommandResult(
                            false, "That processor core preference is no longer offered.");
                    }

                    cores.Apply(mode, cancellationToken);
                    _published = status with { OnAc = mode, OnBattery = mode };
                    _publishWritten = true;
                    _status = string.Empty;
                    return new SteamUiCommandResult(true, null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _publishWritten = _published is not null;
                    _status = $"Selection failed: {ex.Message}";
                    return new SteamUiCommandResult(false, _status);
                }
            }
        }, cancellationToken);
    }

    /// <summary>Publishes a pending write result once, then refreshes Windows core-placement capabilities.</summary>
    /// <returns>
    ///     Available choices and AC selection, or an unavailable state with a reason; the read runs on a worker under the
    ///     same lock as writes.
    /// </returns>
    internal ValueTask<SteamHybridCoreState?> ReadAsync()
    {
        return new ValueTask<SteamHybridCoreState?>(Task.Run<SteamHybridCoreState?>(() =>
        {
            lock (_sync)
            {
                try
                {
                    var status = _publishWritten && _published is not null ? _published : cores.Read();
                    _publishWritten = false;
                    _published = status;
                    if (!status.Supported)
                    {
                        // Published as unavailable rather than withheld. No options hides the row, and
                        // the reason still reaches the component host's render outcomes, so an absent
                        // control can be told from a broken one.
                        return new SteamHybridCoreState(false, [], string.Empty,
                            "This processor has one kind of core, so there is nothing to choose.");
                    }

                    return new SteamHybridCoreState(
                        true,
                        [
                            .. status.Options.Select(option =>
                                new SteamPowerProfileOption(HybridCores.IdFor(option.Mode), option.Name))
                        ],
                        // Empty when the machine is set to a placement WSGM does not offer. Naming one
                        // of its own modes there would claim WSGM put it there.
                        status.OnAc is { } active ? HybridCores.IdFor(active) : string.Empty,
                        string.IsNullOrEmpty(_status) ? Describe(status) : _status);
                }
                catch (Exception ex)
                {
                    return new SteamHybridCoreState(false, [], string.Empty, ex.Message);
                }
            }
        }));
    }

    private static string Describe(HybridCoreStatus status)
    {
        var cores = $"{status.PerformanceCores} performance and {status.EfficiencyCores} efficiency cores.";
        if (status.OnAc is null || status.OnBattery is null)
        {
            return $"{cores} The current preference was not set by WSGM.";
        }

        return status.OnAc == status.OnBattery
            ? $"{cores} Applies to both battery and plugged in."
            : $"{cores} Plugged in and battery currently differ; choosing sets both.";
    }
}
