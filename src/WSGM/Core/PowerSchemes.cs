using System;
using System.Collections.Generic;
using System.Threading;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>An installed Windows power scheme. Only the GUID identifies the scheme.</summary>
/// <param name="Id">Locale-independent identity, suitable for persisted references.</param>
/// <param name="Name">Windows' localized display name.</param>
internal sealed record PowerScheme(Guid Id, string Name);

/// <summary>Manual Windows power-scheme access, independent of device integration.
/// Reads always consult Windows; selections are neither enforced nor restored later.
/// Call from background work when projecting into a UI.</summary>
internal sealed class PowerSchemes(IPowerSchemeApi api)
{
    internal static PowerSchemes Windows { get; } = new(new WindowsPowerSchemeApi());
    internal static object MutationGate { get; } = new();

    internal Guid ReadActive() => api.ReadActive();

    internal IReadOnlyList<PowerScheme> Enumerate()
    {
        List<PowerScheme> schemes = [];
        for (uint index = 0; ; index++)
        {
            Guid? id = api.Enumerate(index);
            if (id is null)
            {
                return schemes.AsReadOnly();
            }
            schemes.Add(new PowerScheme(id.Value, api.ReadName(id.Value)));
        }
    }

    /// <summary>Writes once and verifies the active GUID. A failed write or readback throws;
    /// the write may already have taken effect, so the caller must re-read before another action.</summary>
    internal void Select(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A power scheme GUID is required.", nameof(id));
        }
        lock (MutationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            api.SetActive(id);
            Guid active = api.ReadActive();
            if (active != id)
            {
                throw new InvalidOperationException(
                    $"Windows power scheme selection was not confirmed: requested {id:D}, active {active:D}.");
            }
        }
    }
}
