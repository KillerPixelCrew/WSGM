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
internal sealed class NativeQamPowerProfileService : ISteamPowerProfileBackend
{
    private static readonly TimeSpan SchemeRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly Action<Guid> _persist;
    private readonly PowerSchemes _schemes;
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private HashSet<Guid> _offeredIds = [];
    private SteamPowerProfileOption[] _options = [];
    private DateTimeOffset _refreshAfter;
    private bool _refreshRequested = true;
    private string _status = string.Empty;
    private Guid? _writtenActive;

    internal NativeQamPowerProfileService(
        PowerSchemes schemes,
        Action<Guid> persist,
        TimeProvider? timeProvider = null)
    {
        _schemes = schemes;
        _persist = persist;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(option, "D", out var id) || id == Guid.Empty)
        {
            return new SteamUiCommandResult(false, "Invalid power-profile GUID.");
        }

        try
        {
            var saveError = await SelectAsync(id, cancellationToken);
            return new SteamUiCommandResult(saveError is null, saveError);
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

    /// <summary>Selects once, persists once and returns a save failure after an accepted native write.</summary>
    internal Task<string?> SelectAsync(Guid id, CancellationToken cancellationToken)
    {
        return Task.Run<string?>(() =>
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
                        try
                        {
                            _persist(id);
                        }
                        catch (Exception ex)
                        {
                            _status =
                                $"Windows applied the profile, but WSGM could not save the reference: {ex.Message}";
                            return _status;
                        }
                    }

                    _status = string.Empty;
                    return null;
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
            .. schemes.Select(scheme => new SteamPowerProfileOption(scheme.Id.ToString("D"),
                nameCounts[scheme.Name] > 1
                    ? $"{scheme.Name} ({scheme.Id:D})"
                    : scheme.Name))
        ];
        _refreshAfter = _timeProvider.GetUtcNow() + SchemeRefreshInterval;
        _refreshRequested = false;
        return schemes;
    }

    internal ValueTask<SteamPowerProfileState?> ReadAsync()
    {
        return new ValueTask<SteamPowerProfileState?>(Task.Run<SteamPowerProfileState?>(() =>
        {
            lock (_sync)
            {
                try
                {
                    if (_writtenActive is { } written)
                    {
                        _writtenActive = null;
                        return new SteamPowerProfileState(PowerSchemes.OffersChoice(_options.Length), _options,
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
                        return new SteamPowerProfileState(offered, offered ? _options : [], string.Empty, ex.Message);
                    }

                    // One profile is nothing to choose: no options publishes no row, the way a
                    // processor with one kind of core publishes none.
                    if (!PowerSchemes.OffersChoice(_options.Length))
                    {
                        return new SteamPowerProfileState(false, [], active.ToString("D"),
                            "Windows offers one power profile.");
                    }

                    return new SteamPowerProfileState(true, _options,
                        active.ToString("D"), string.IsNullOrEmpty(_status)
                            ? "Windows controls the active power profile. Changes apply immediately."
                            : _status);
                }
                catch (Exception ex)
                {
                    _refreshRequested = true;
                    return new SteamPowerProfileState(false, [], string.Empty, ex.Message);
                }
            }
        }));
    }
}
