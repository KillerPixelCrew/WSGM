using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Windows power profiles for Steam's Performance dropdown. Every publication reads the active
///     profile; the installed list is cached briefly, and commands never retry a write.
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
    private bool _requiresRead = true;
    private string _status = string.Empty;

    internal NativeQamPowerProfileService(
        PowerSchemes schemes,
        Action<Guid> persist,
        TimeProvider? timeProvider = null)
    {
        _schemes = schemes;
        _persist = persist;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(option, "D", out var id) || id == Guid.Empty)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Invalid power-profile GUID."));
        }

        return Task.Run(() =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_requiresRead)
                {
                    return new SteamUiCommandResult(false, "Windows state must be refreshed before another selection.");
                }

                try
                {
                    if (!_offeredIds.Contains(id))
                    {
                        return new SteamUiCommandResult(false, "The power profile is no longer installed.");
                    }

                    lock (PowerSchemes.MutationGate)
                    {
                        _schemes.Select(id, cancellationToken);
                        _requiresRead = true;
                        try
                        {
                            _persist(id);
                        }
                        catch (Exception ex)
                        {
                            _status =
                                $"Windows applied the profile, but WSGM could not save the reference: {ex.Message}";
                            return new SteamUiCommandResult(false, _status);
                        }
                    }

                    _status = string.Empty;
                    return new SteamUiCommandResult(true, null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _requiresRead = true;
                    _status = $"Selection was not confirmed: {ex.Message}";
                    return new SteamUiCommandResult(false, _status);
                }
            }
        }, cancellationToken);
    }

    internal ValueTask<SteamPowerProfileState?> ReadAsync()
    {
        return new ValueTask<SteamPowerProfileState?>(Task.Run<SteamPowerProfileState?>(() =>
        {
            lock (_sync)
            {
                try
                {
                    if (_requiresRead || _timeProvider.GetUtcNow() >= _refreshAfter)
                    {
                        var schemes = _schemes.Enumerate();
                        if (schemes.Count > 64)
                        {
                            return new SteamPowerProfileState(false, [], string.Empty,
                                "Windows returned more than 64 power profiles.");
                        }

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
                        _requiresRead = false;
                    }

                    var active = _schemes.ReadActive();
                    return new SteamPowerProfileState(_options.Length > 0, _options,
                        active.ToString("D"), string.IsNullOrEmpty(_status)
                            ? "Windows controls the active power profile. Changes apply immediately."
                            : _status);
                }
                catch (Exception ex)
                {
                    _requiresRead = true;
                    return new SteamPowerProfileState(false, [], string.Empty, ex.Message);
                }
            }
        }));
    }
}
