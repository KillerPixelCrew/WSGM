using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The one owner of the power button's short-press choice that the overlay's Power page and Steam's
///     Power settings page both show and change.
/// </summary>
/// <remarks>
///     The choice is offered only while the device actually handles the button, which a model that cannot
///     read it never does; with Device Integration off there is no device and nothing to offer.
/// </remarks>
internal sealed class PowerButtonSetting
{
    /// <summary>Desktop choices; Game Mode always uses Steam suspend on a supported device.</summary>
    internal static readonly PowerButtonShortPress[] Choices =
        Enum.GetValues<PowerButtonShortPress>().Where(value => value != PowerButtonShortPress.SteamSuspend).ToArray();

    private readonly DeviceCoordinator? _device;

    /// <param name="device">The session's device coordinator, or null with Device Integration off.</param>
    internal PowerButtonSetting(DeviceCoordinator? device)
    {
        _device = device;
        if (device is not null)
        {
            device.PowerButton.ActiveChanged += () => Changed?.Invoke();
        }
    }

    /// <summary>Raised on any thread when availability or the choice changes.</summary>
    internal event Action? Changed;

    /// <summary>Whether this model's power button is handled now.</summary>
    internal bool Available => _device?.PowerButton.Active == true;

    /// <summary>The chosen short-press action.</summary>
    internal PowerButtonShortPress Current => _device?.PowerButtonShortPress is { } value
        && value != PowerButtonShortPress.SteamSuspend ? value : PowerButtonShortPress.Sleep;

    /// <summary>Short user-facing label for a choice.</summary>
    /// <param name="value">The choice.</param>
    /// <returns>Its label.</returns>
    internal static string Label(PowerButtonShortPress value)
    {
        return value switch
        {
            PowerButtonShortPress.Sleep => "Sleep",
            PowerButtonShortPress.SteamSuspend => "Steam suspend (Game Mode)",
            PowerButtonShortPress.Hibernate => "Hibernate",
            PowerButtonShortPress.Shutdown => "Shut down",
            PowerButtonShortPress.Restart => "Restart",
            PowerButtonShortPress.Nothing => "Do nothing",
            _ => value.ToString()
        };
    }

    /// <summary>Saves and applies a new choice.</summary>
    /// <param name="value">The chosen action.</param>
    /// <param name="cancellationToken">Cancels the save before it starts.</param>
    /// <returns>Whether it was saved; false when no device handles the button.</returns>
    internal async Task<bool> SelectAsync(PowerButtonShortPress value, CancellationToken cancellationToken)
    {
        if (_device is not { } device || !Available)
        {
            return false;
        }

        await device.SetPowerButtonShortPressAsync(value, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
        return true;
    }
}
