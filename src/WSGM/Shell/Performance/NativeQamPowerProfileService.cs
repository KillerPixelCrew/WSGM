using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The session's Windows power-profile workflow for Steam and the overlay. Accepted writes are published first;
///     independent refreshes read Windows. The installed list is cached briefly and writes are not retried.
/// </summary>
internal sealed class NativeQamPowerProfileService
{
    private static readonly TimeSpan SchemeRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly PowerSchemes _schemes;
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private HashSet<Guid> _offeredIds = [];
    private ChoiceRowOption[] _options = [];
    private DateTimeOffset _refreshAfter;
    private bool _refreshRequested = true;
    private string _status = string.Empty;
    private Guid? _writtenActive;

    /// <summary>Creates the shared selection workflow; the first list read establishes which profiles may be selected.</summary>
    /// <param name="schemes">Borrowed Windows scheme owner and mutation gate.</param>
    /// <param name="timeProvider">Clock for the one-minute option cache, or null for the system clock.</param>
    internal NativeQamPowerProfileService(
        PowerSchemes schemes,
        TimeProvider? timeProvider = null)
    {
        _schemes = schemes;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Selects one offered Windows power profile for Steam's rows.</summary>
    /// <param name="option">The profile's GUID in "D" form.</param>
    /// <param name="cancellationToken">Cancels admission and selection.</param>
    /// <returns>Applied, or the refusal with its reason.</returns>
    /// <remarks>Invalid IDs and operational failures become refusal results; cancellation propagates.</remarks>
    internal async Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(option, "D", out var id) || id == Guid.Empty)
        {
            return new SteamUiCommandResult(false, "Invalid power-profile GUID.");
        }

        try
        {
            await SelectAsync(id, cancellationToken);
            return new SteamUiCommandResult(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
    }

    /// <summary>Selects once; Windows keeps the active scheme, so nothing is saved.</summary>
    /// <param name="id">Nonempty scheme GUID present in the latest offered list.</param>
    /// <param name="cancellationToken">Cancels admission and scheme selection; it cannot undo an accepted native write.</param>
    /// <returns>Completion once Windows accepted the selection.</returns>
    /// <exception cref="InvalidOperationException">The GUID is not offered or Windows selection failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed before the write.</exception>
    internal Task SelectAsync(Guid id, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_offeredIds.Contains(id))
                {
                    throw new InvalidOperationException("The power profile is no longer installed.");
                }

                try
                {
                    using (_schemes.EnterMutation())
                    {
                        _schemes.Select(id, cancellationToken);
                        _refreshRequested = true;
                        _writtenActive = id;
                    }

                    _status = string.Empty;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _status = $"Selection failed: {ex.Message}";
                    throw new InvalidOperationException(_status, ex);
                }
            }
        }, cancellationToken);
    }

    /// <summary>Refreshes the overlay's list and active GUID without selecting or persisting anything.</summary>
    /// <param name="cancellationToken">Cancels admission before the synchronous worker reads.</param>
    /// <returns>
    ///     Installed schemes and active GUID; an active-read failure retains the options with null Active and its error
    ///     detail.
    /// </returns>
    /// <remarks>Enumeration failures fault the task. This forces a list refresh, bypassing the Steam cache interval.</remarks>
    internal Task<(IReadOnlyList<PowerScheme> Items, Guid? Active, string? Detail)> ReadSchemesAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run<(IReadOnlyList<PowerScheme> Items, Guid? Active, string? Detail)>(() =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = RefreshSchemes();
                try
                {
                    return (items, _schemes.ReadActive(), null);
                }
                catch (Exception ex)
                {
                    // An unreadable active GUID does not prevent an explicit selection of an installed plan.
                    return (items, null, ex.Message);
                }
            }
        }, cancellationToken);
    }

    private IReadOnlyList<PowerScheme> RefreshSchemes()
    {
        var schemes = _schemes.Enumerate();
        Dictionary<string, int> nameCounts = new(StringComparer.Ordinal);
        foreach (var scheme in schemes)
        {
            nameCounts.TryGetValue(scheme.Name, out var count);
            nameCounts[scheme.Name] = count + 1;
        }

        _offeredIds = [.. schemes.Select(scheme => scheme.Id)];
        _options =
        [
            .. schemes.Select(scheme => new ChoiceRowOption(scheme.Id.ToString("D"),
                nameCounts[scheme.Name] > 1
                    ? $"{scheme.Name} ({scheme.Id:D})"
                    : scheme.Name))
        ];
        _refreshAfter = _timeProvider.GetUtcNow() + SchemeRefreshInterval;
        _refreshRequested = false;
        return schemes;
    }

    /// <summary>Publishes one accepted selection without readback, then resumes independent state reads.</summary>
    /// <returns>
    ///     Steam choices and status; read failures produce unavailable state or preserve options when only active lookup
    ///     fails.
    /// </returns>
    /// <remarks>Work is serialized on a worker. Options refresh when requested or after the one-minute cache interval.</remarks>
    internal ValueTask<ChoiceRowState?> ReadAsync()
    {
        return new ValueTask<ChoiceRowState?>(Task.Run<ChoiceRowState?>(() =>
        {
            lock (_sync)
            {
                try
                {
                    if (_writtenActive is { } written)
                    {
                        _writtenActive = null;
                        return new ChoiceRowState(PowerSchemes.OffersChoice(_options.Length), _options,
                            written.ToString("D"), string.IsNullOrEmpty(_status)
                                ? "Windows accepted the power profile selection."
                                : _status);
                    }

                    if (_refreshRequested || _timeProvider.GetUtcNow() >= _refreshAfter)
                    {
                        RefreshSchemes();
                    }

                    Guid active;
                    try
                    {
                        active = _schemes.ReadActive();
                    }
                    catch (Exception ex)
                    {
                        var offered = PowerSchemes.OffersChoice(_options.Length);
                        return new ChoiceRowState(offered, offered ? _options : [], string.Empty, ex.Message);
                    }

                    if (!PowerSchemes.OffersChoice(_options.Length))
                    {
                        return new ChoiceRowState(false, [], active.ToString("D"),
                            "Windows offers one power profile.");
                    }

                    return new ChoiceRowState(true, _options,
                        active.ToString("D"), string.IsNullOrEmpty(_status)
                            ? "Windows controls the active power profile. Changes apply immediately."
                            : _status);
                }
                catch (Exception ex)
                {
                    _refreshRequested = true;
                    return new ChoiceRowState(false, [], string.Empty, ex.Message);
                }
            }
        }));
    }
}
