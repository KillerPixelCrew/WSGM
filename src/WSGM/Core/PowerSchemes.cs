using System;
using System.Collections.Generic;
using System.Threading;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>An installed Windows power scheme. Only the GUID identifies the scheme.</summary>
/// <param name="Id">Locale-independent identity, suitable for persisted references.</param>
/// <param name="Name">Windows' localized display name.</param>
internal sealed record PowerScheme(Guid Id, string Name);

/// <summary>
///     Manual Windows power-scheme access, independent of device integration.
///     Reads always consult Windows; selections are neither enforced nor restored later.
///     Call from background work when projecting into a UI.
/// </summary>
/// <remarks>
///     The session builds one instance (see <see cref="WindowsPowerPolicy" />) and hands it to every owner
///     that changes machine-wide power policy. Its mutation lock serializes those changes, because each one
///     writes the active scheme or re-activates it, and a scheme switch between another owner's read and
///     write would land a value in a scheme that is no longer active.
/// </remarks>
internal sealed class PowerSchemes(IPowerSchemeApi api)
{
    private readonly Lock _mutation = new();

    /// <summary>Whether a picker is worth showing: one plan is nothing to choose.</summary>
    /// <param name="count">How many plans Windows enumerates.</param>
    internal static bool OffersChoice(int count)
    {
        return count > 1;
    }

    /// <summary>
    ///     Enters the one lock over machine-wide power changes. Reentrant, so an owner holding it may call
    ///     another owner that takes it too. Dispose the scope to leave.
    /// </summary>
    internal Lock.Scope EnterMutation()
    {
        return _mutation.EnterScope();
    }

    internal Guid ReadActive()
    {
        return api.ReadActive();
    }

    /// <summary>Re-activates the active scheme so values written to it take effect. Call under the mutation lock.</summary>
    internal void RefreshActive()
    {
        api.SetActive(api.ReadActive());
    }

    /// <summary>Reads one AC or battery policy value of a scheme. Native failures propagate.</summary>
    internal uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
    {
        return api.ReadSetting(scheme, subgroup, setting, onBattery);
    }

    /// <summary>Writes one AC or battery policy value of a scheme once. Call under the mutation lock.</summary>
    internal void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
    {
        api.WriteSetting(scheme, subgroup, setting, onBattery, value);
    }

    internal IReadOnlyList<PowerScheme> Enumerate()
    {
        List<PowerScheme> schemes = [];
        for (uint index = 0;; index++)
        {
            var id = api.Enumerate(index);
            if (id is null)
            {
                return schemes.AsReadOnly();
            }

            schemes.Add(new PowerScheme(id.Value, api.ReadName(id.Value)));
        }
    }

    /// <summary>
    ///     Writes once. A native failure propagates; an accepted selection needs no confirming read.
    /// </summary>
    internal void Select(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A power scheme GUID is required.", nameof(id));
        }

        using (EnterMutation())
        {
            cancellationToken.ThrowIfCancellationRequested();
            api.SetActive(id);
        }
    }
}
