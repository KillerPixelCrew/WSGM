using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Settings;

/// <summary>One authored device profile being edited.</summary>
/// <remarks>
/// Holds the curve in the SDK's own point type because that is what the editor control speaks;
/// conversion to the stored shape happens once, at save. Keeping two mutable representations in
/// step during a drag is exactly the kind of bookkeeping that goes wrong silently.
/// </remarks>
public sealed class DeviceProfileRowViewModel : INotifyPropertyChanged
{
    private string _name;
    private IReadOnlyList<CurvePoint> _curve;

    /// <summary>Creates a row from a stored profile.</summary>
    /// <param name="profile">The stored profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public DeviceProfileRowViewModel(DeviceAuthoredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileId = profile.ProfileId;
        CapabilityId = profile.CapabilityId;
        _name = profile.Name;
        _curve =
        [
            .. profile.Curve.Select(point => new CurvePoint(point.Input, point.Output)),
        ];
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stable identifier the overlay selects by. Never changes with a rename.</summary>
    public string ProfileId { get; }

    /// <summary>The capability this profile authors.</summary>
    public string CapabilityId { get; }

    /// <summary>Gets or sets what the user calls it.</summary>
    public string Name
    {
        get => _name;
        set
        {
            string bounded = (value ?? string.Empty).Trim();
            if (bounded.Length > DeviceAuthoredProfile.MaxNameLength)
            {
                bounded = bounded[..DeviceAuthoredProfile.MaxNameLength];
            }

            if (string.Equals(_name, bounded, StringComparison.Ordinal))
            {
                return;
            }

            _name = bounded;
            Raise(nameof(Name));
        }
    }

    /// <summary>Gets or sets the curve, as the editor control works with it.</summary>
    public IReadOnlyList<CurvePoint> Curve
    {
        get => _curve;
        set
        {
            _curve = value ?? [];
            Raise(nameof(Curve));
        }
    }

    /// <summary>Converts back to the stored shape.</summary>
    /// <returns>The profile to persist.</returns>
    /// <remarks>
    /// A rename keeps <see cref="ProfileId"/>, which is the entire reason the two are separate: an
    /// application override points at the id, and renaming a profile must not orphan it.
    /// </remarks>
    public DeviceAuthoredProfile ToStored() => new()
    {
        ProfileId = ProfileId,
        Name = string.IsNullOrWhiteSpace(_name) ? ProfileId : _name,
        CapabilityId = CapabilityId,
        Curve =
        [
            .. _curve.Select(point => new AuthoredCurvePoint
            {
                Input = point.Input,
                Output = point.Output,
            }),
        ],
    };

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
