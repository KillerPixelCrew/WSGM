using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Windows power profiles for Steam's Performance dropdown. Every publication reads
/// Windows; commands validate the offered GUID and never retry a write.</summary>
internal sealed class NativeQamPowerProfileService(PowerSchemes schemes, Action<Guid> persist) : ISteamPowerProfileBackend
{
    private readonly object _sync = new();
    private string _status = string.Empty;
    private bool _requiresRead;

    internal ValueTask<SteamPowerProfileState?> ReadAsync() => new(Task.Run<SteamPowerProfileState?>(() =>
    {
        lock (_sync)
        {
            try
            {
                var options = schemes.Enumerate();
                Guid active = schemes.ReadActive();
                if (options.Count > 64)
                {
                    return new(false, [], string.Empty, "Windows returned more than 64 power profiles.");
                }
                _requiresRead = false;
                return new(options.Count > 0,
                    options.Select(scheme => new SteamPowerProfileOption(scheme.Id.ToString("D"),
                        options.Count(other => other.Name == scheme.Name) > 1
                            ? $"{scheme.Name} ({scheme.Id:D})" : scheme.Name)).ToArray(),
                    active.ToString("D"), string.IsNullOrEmpty(_status)
                        ? "Windows controls the active power profile. Changes apply immediately." : _status);
            }
            catch (Exception ex)
            {
                _requiresRead = true;
                return new(false, [], string.Empty, ex.Message);
            }
        }
    }));

    public Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(option, "D", out Guid id) || id == Guid.Empty)
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
                    if (!schemes.Enumerate().Any(scheme => scheme.Id == id))
                    {
                        return new SteamUiCommandResult(false, "The power profile is no longer installed.");
                    }
                    lock (PowerSchemes.MutationGate)
                    {
                        schemes.Select(id, cancellationToken);
                        try { persist(id); }
                        catch (Exception ex)
                        {
                            _status = $"Windows applied the profile, but WSGM could not save the reference: {ex.Message}";
                            return new SteamUiCommandResult(false, _status);
                        }
                    }
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
