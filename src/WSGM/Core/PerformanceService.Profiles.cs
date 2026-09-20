using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

internal sealed partial class PerformanceService
{
    internal IReadOnlyList<PerformanceApplicationPolicy> Profiles
    {
        get
        {
            lock (_stateGate)
            {
                return _policy.Applications.ToArray();
            }
        }
    }

    internal Task<bool> SetApplicationProfileEnabledAsync(bool enabled,
        CancellationToken cancellationToken = default, string? expectedApplicationId = null)
    {
        return EditProfilesAsync((policy, target) =>
        {
            if (target is null || (expectedApplicationId is not null && target.ApplicationId != expectedApplicationId))
            {
                return policy;
            }

            var existing = PerformancePolicyResolver.FindStored(policy, target);
            if ((existing?.Enabled ?? false) == enabled)
            {
                return policy;
            }

            var entry = existing is not null
                ? existing with { Enabled = enabled }
                : new PerformanceApplicationPolicy(
                    policy.Applications.Any(item => item.ApplicationId == target.ApplicationId)
                        ? "profile:" + Guid.NewGuid().ToString("N")
                        : target.ApplicationId,
                    target.RtssProfileName ?? string.Empty,
                    PerformancePolicyResolver.Resolve(policy, target).Values)
                {
                    Name = target.RtssProfileName ?? target.ApplicationId,
                    ProcessNames = target.RtssProfileName is { Length: > 0 } executable ? [executable] : []
                };
            return ReplaceProfile(policy, entry);
        }, cancellationToken);
    }

    internal Task<bool> SaveProfileAsync(PerformanceApplicationPolicy profile,
        CancellationToken cancellationToken = default)
    {
        var name = profile.Name.Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Give the profile a name of 1 to 80 characters.");
        }

        var processes = ApplicationProfileRules.ValidateProcesses(profile.ProcessNames);
        if (profile.Values.FrameLimit is < 0 or > 1000 || profile.Values.OverlayLevel is < 0 or > 4)
        {
            throw new ArgumentException("Frame limit must be 0 to 1000, and overlay level 0 to 4.");
        }

        return EditProfilesAsync((policy, _) =>
        {
            var existing = policy.Applications.FirstOrDefault(item => item.ApplicationId == profile.ApplicationId);
            if (processes.Length == 0 &&
                (existing is null || profile.ApplicationId.StartsWith("profile:", StringComparison.Ordinal)))
            {
                throw new ArgumentException("Add at least one executable that activates this profile.");
            }

            var conflict = policy.Applications.FirstOrDefault(item => item.ApplicationId != profile.ApplicationId
                                                                      && item.ProcessNames.Intersect(processes,
                                                                          StringComparer.OrdinalIgnoreCase).Any());
            if (conflict is not null)
            {
                throw new ArgumentException(
                    $"An executable is already assigned to {(string.IsNullOrEmpty(conflict.Name) ? conflict.ApplicationId : conflict.Name)}.");
            }

            return ReplaceProfile(policy, profile with { Name = name, ProcessNames = processes });
        }, cancellationToken);
    }

    internal Task<bool> DeleteProfileAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        return EditProfilesAsync((policy, _) => policy with
        {
            Applications = policy.Applications.Where(item => item.ApplicationId != applicationId).ToArray()
        }, cancellationToken);
    }

    internal Task<bool> ResetProfileAsync(CancellationToken cancellationToken = default)
    {
        return EditProfilesAsync((policy, target) => PerformancePolicyResolver.Find(policy, target) is { } entry
            ? ReplaceProfile(policy, entry with { Values = PerformanceValues.Empty })
            : policy with { Global = PerformanceValues.Empty }, cancellationToken);
    }

    private static PerformancePolicy ReplaceProfile(PerformancePolicy policy, PerformanceApplicationPolicy profile)
    {
        var entries = policy.Applications.ToList();
        var index = entries.FindIndex(item => item.ApplicationId == profile.ApplicationId);
        if (index < 0)
        {
            entries.Add(profile);
        }
        else
        {
            entries[index] = profile;
        }

        return policy with { Applications = entries.ToArray() };
    }

    private async Task<bool> EditProfilesAsync(
        Func<PerformancePolicy, PerformanceApplicationTarget?, PerformancePolicy> edit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var apply = false;
        await _adapterGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            PerformancePolicy changed;
            lock (_stateGate)
            {
                changed = NormalizePolicy(edit(_policy, _state.Target));
                if (PoliciesEqual(_policy, changed))
                {
                    return false;
                }
            }

            // ConfigStore performs synchronous disk work; never run it on the overlay dispatcher.
            await Task.Run(() => _persistPolicy(changed, lifetime.Token), lifetime.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                _policy = changed;
                var next = WithResolvedDesired(_state);
                apply = next.Desired != _state.Desired
                        || next.ApplicationProfileEnabled != _state.ApplicationProfileEnabled;
                _state = next;
            }
        }
        finally
        {
            _adapterGate.Release();
        }

        Interlocked.Exchange(ref _raisedState, null);
        RaiseStateChanged(Current);
        if (apply)
        {
            await ApplyEffectiveDesiredAsync("profile-edit", lifetime.Token).ConfigureAwait(false);
        }

        return true;
    }
}
