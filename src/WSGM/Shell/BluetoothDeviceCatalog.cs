using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Shell;

internal sealed record BluetoothLogicalDevice(
    string Id, string EndpointId, string PairingEndpointId, string Name, string Container,
    bool Paired, bool CanPair, bool Connected, IReadOnlyList<string> EndpointIds);

/// <summary>Merges watcher endpoints into the one logical collection used by every WSGM surface.</summary>
internal sealed class BluetoothDeviceCatalog
{
    private readonly Dictionary<string, WindowsRadio.BluetoothDevice> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    internal void BeginSweep() => _seen.Clear();

    internal void ConfirmPairing(string endpointId, bool paired)
    {
        if (_endpoints.TryGetValue(endpointId, out var current))
        {
            _endpoints[endpointId] = current with { Paired = paired, Connected = paired && current.Connected };
        }
    }

    internal IReadOnlyList<BluetoothLogicalDevice> Apply(WindowsRadio.BluetoothChangeKind kind, WindowsRadio.BluetoothDevice device)
    {
        if (kind == WindowsRadio.BluetoothChangeKind.EnumerationCompleted)
        {
            foreach (var old in _endpoints.Values.ToArray())
            {
                if (!_seen.Contains(old.Id)) { Remove(old.Id); }
            }
        }
        else if (!string.IsNullOrEmpty(device.Id))
        {
            if (kind == WindowsRadio.BluetoothChangeKind.Removed) { Remove(device.Id); }
            else
            {
                _seen.Add(device.Id);
                _endpoints[device.Id] = device;
            }
        }
        return Snapshot();
    }

    private void Remove(string id)
    {
        if (_endpoints.TryGetValue(id, out var previous) && previous.Paired)
        { _endpoints[id] = previous with { Connected = false }; }
        else { _endpoints.Remove(id); }
    }

    private IReadOnlyList<BluetoothLogicalDevice> Snapshot() => _endpoints.Values
        .GroupBy(device => Container(device.Container) is { Length: > 0 } container
            ? $"container:{container}" : $"endpoint:{device.Id}", StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var members = group.OrderByDescending(d => d.Paired).ThenByDescending(d => d.Connected)
                .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            var selected = members[0];
            var pairable = members.FirstOrDefault(d => d.CanPair && !d.Paired);
            return new BluetoothLogicalDevice(group.Key, selected.Id,
                string.IsNullOrEmpty(pairable.Id) ? selected.Id : pairable.Id,
                members.Select(d => d.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unnamed device",
                Container(selected.Container), members.Any(d => d.Paired), members.Any(d => d.CanPair),
                members.Any(d => d.Connected), members.Select(d => d.Id).ToArray());
        }).OrderBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Container(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D") : string.Empty;
}
