using System.Collections.Generic;
using WSGM.Controls;
using WSGM.Core;
using WifiSecurity = WindowsDeviceControl.WindowsRadio.WifiSecurity;

namespace WSGM.Shell;

/// <summary>
///     One row in the Wi-Fi list. A row instance survives refreshes so the
///     gamepad cursor keeps its place; only its values are updated.
/// </summary>
public sealed class WifiNetworkEntry : ObservableObject
{
    /// <summary>Creates a row for a network.</summary>
    /// <param name="ssid">The network name, which also identifies the row.</param>
    public WifiNetworkEntry(string ssid)
    {
        Ssid = ssid;
    }

    /// <summary>Gets the network name. Immutable: it is the row's identity.</summary>
    public string Ssid { get; }

    /// <summary>Gets the signal quality, 0-100.</summary>
    public int Signal
    {
        get;
        internal set => SetFieldIfChanged(ref field, value, nameof(Signal));
    }

    /// <summary>Gets how the network is protected.</summary>
    public WifiSecurity Security
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Security)))
            {
                return;
            }
            Raise(nameof(NeedsPassword));
            Raise(nameof(Secured));
            Raise(nameof(StatusLine));
            Raise(nameof(ActionEnabled));
        }
    }

    /// <summary>Gets whether a saved profile exists, so joining needs no password.</summary>
    public bool Saved
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Saved)))
            {
                return;
            }
            Raise(nameof(NeedsPassword));
            Raise(nameof(StatusLine));
        }
    }

    /// <summary>
    ///     Gets whether the driver believes this network can be joined at
    ///     all. False leaves the row visible but its action disabled: offering a
    ///     Connect the driver has already rejected produces a doomed attempt with
    ///     nothing to explain it.
    /// </summary>
    public bool Connectable
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Connectable)))
            {
                return;
            }
            Raise(nameof(ActionEnabled));
            Raise(nameof(StatusLine));
        }
    } = true;

    /// <summary>
    ///     Gets whether the row's action button may be pressed. A joined
    ///     network can always be disconnected, whatever the scan says about
    ///     joining it again — but an enterprise network is never joinable here (it
    ///     needs an EAP flow this panel does not offer), and an enabled button that
    ///     silently does nothing is worse than a disabled one. WEP
    ///     (<see cref="WifiSecurity.Unsupported" />) is listed but not offered: its
    ///     open-system authentication otherwise looks exactly like an unsecured
    ///     network, so it would skip the password prompt and then fail.
    /// </summary>
    public bool ActionEnabled => Connected
                                 || (Connectable
                                     && Security != WifiSecurity.Enterprise
                                     && Security != WifiSecurity.Unsupported);

    /// <summary>Gets whether this is the network currently joined.</summary>
    public bool Connected
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Connected)))
            {
                return;
            }
            Raise(nameof(IconState));
            Raise(nameof(StatusLine));
            Raise(nameof(ActionText));
            Raise(nameof(ActionEnabled));
        }
    }

    /// <summary>
    ///     Gets whether joining this network needs a password prompt: it is
    ///     secured, and no saved profile already carries the key. Enhanced Open is
    ///     encrypted but keyless, so it never prompts.
    /// </summary>
    public bool NeedsPassword => Security == WifiSecurity.PersonalPsk && !Saved;

    /// <summary>Gets whether the network is protected at all.</summary>
    public bool Secured => Security != WifiSecurity.Open;

    /// <summary>
    ///     Gets whether this row is showing its actions. Selecting a row
    ///     reveals what can be done with it rather than acting immediately — a tap
    ///     must never disconnect the network the user is using.
    /// </summary>
    public bool Expanded
    {
        get;
        internal set => SetFieldIfChanged(ref field, value, nameof(Expanded));
    }

    /// <summary>
    ///     Gets the icon state: off is never used here (a listed network
    ///     implies a live radio), so this is connected or merely visible.
    /// </summary>
    public RadioIconState IconState => Connected
        ? RadioIconState.Connected
        : RadioIconState.Disconnected;

    /// <summary>Gets the second line under the name.</summary>
    public string StatusLine => Connected
        ? "Connected"
        : !Connectable
            ? "Not available right now"
            : Security switch
            {
                WifiSecurity.Enterprise => "Enterprise network (not supported here)",
                WifiSecurity.Unsupported => "WEP network (not supported here)",
                WifiSecurity.Open => Saved ? "Open, saved" : "Open",
                WifiSecurity.EnhancedOpen => Saved ? "Open (encrypted), saved" : "Open (encrypted)",
                _ => Saved ? "Saved" : "Secured"
            };

    /// <summary>Gets the label for this row's action button.</summary>
    public string ActionText => Connected ? "Disconnect" : "Connect";
}

/// <summary>
///     One row in the Bluetooth list. Same in-place refresh discipline as
///     <see cref="WifiNetworkEntry" />.
/// </summary>
public sealed class BluetoothDeviceEntry : ObservableObject
{
    /// <summary>Creates a row for a device.</summary>
    /// <param name="id">The stable logical device id.</param>
    public BluetoothDeviceEntry(string id)
    {
        Id = id;
        EndpointId = id;
        PairingEndpointId = id;
    }

    /// <summary>Gets the logical identity shared by Overlay and Steam, independent of Windows endpoint selection.</summary>
    public string Id { get; }

    /// <summary>Gets the Windows association endpoint selected for operations on a paired device.</summary>
    public string EndpointId { get; internal set; }

    /// <summary>Gets the current pairable Windows endpoint.</summary>
    public string PairingEndpointId { get; internal set; }

    internal IReadOnlyList<string> EndpointIds { get; set; } = [];

    /// <summary>
    ///     Gets the display name, or a placeholder when the device has not
    ///     advertised one yet.
    /// </summary>
    public string Name
    {
        get => field.Length == 0 ? "Unnamed device" : field;
        internal set => SetFieldIfChanged(ref field, value, nameof(Name));
    } = "";

    /// <summary>Gets whether the device is paired.</summary>
    public bool Paired
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Paired)))
            {
                return;
            }
            Raise(nameof(ActionText));
            Raise(nameof(IconState));
            Raise(nameof(StatusLine));
            Raise(nameof(PrimaryActionVisible));
            Raise(nameof(RemoveVisible));
        }
    }

    /// <summary>Gets whether Windows believes pairing is currently possible.</summary>
    public bool CanPair
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(CanPair)))
            {
                return;
            }
            Raise(nameof(StatusLine));
            // A device that enters pairing mode later must reveal its Pair
            // button without the row being rebuilt.
            Raise(nameof(PrimaryActionVisible));
        }
    }

    /// <summary>
    ///     Gets whether the device has a live connection right now. Paired
    ///     and connected are different states: a paired headset that is switched
    ///     off must not read as "connected".
    /// </summary>
    public bool Connected
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Connected)))
            {
                return;
            }
            Raise(nameof(IconState));
            Raise(nameof(StatusLine));
            Raise(nameof(ActionText));
        }
    }

    /// <summary>
    ///     Gets the device container id, which ties the device to its
    ///     audio endpoints. Empty when Windows reported none.
    /// </summary>
    public string ContainerId
    {
        get;
        internal set => SetFieldIfChanged(ref field, value, nameof(ContainerId));
    } = "";

    /// <summary>
    ///     Gets whether this device can be connected/disconnected on
    ///     demand — true only for devices with audio endpoints. Everything else
    ///     (mice, gamepads) reconnects on its own initiative when used, and
    ///     Windows offers no general reconnect operation for them; the row then shows only
    ///     Pair or Remove, the same choice the Settings app makes.
    /// </summary>
    public bool AudioConnectable
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(AudioConnectable)))
            {
                return;
            }
            Raise(nameof(ActionText));
            Raise(nameof(PrimaryActionVisible));
        }
    }

    /// <summary>
    ///     Gets whether this device's AUDIO endpoints are live, which is
    ///     what the connect action actually toggles. Deliberately separate from
    ///     <see cref="Connected" />: a headset can hold an association for another
    ///     profile while its audio endpoints sit unplugged, and reading the broader
    ///     state there would label the button Disconnect and then send the opposite
    ///     one-shot.
    /// </summary>
    public bool AudioActive
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(AudioActive)))
            {
                return;
            }
            Raise(nameof(ActionText));
        }
    }

    /// <summary>Gets whether an operation is in flight for this device.</summary>
    public bool Busy
    {
        get;
        internal set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Busy)))
            {
                return;
            }
            Raise(nameof(ActionText));
            Raise(nameof(StatusLine));
        }
    }

    /// <summary>
    ///     Gets the label for this row's primary button: Pair for a
    ///     stranger; Connect/Disconnect for a paired audio device. The pairing
    ///     itself is only ever touched by the separate Remove button.
    /// </summary>
    public string ActionText => Busy
        ? "Working..."
        : !Paired
            ? "Pair"
            : AudioActive
                ? "Disconnect"
                : "Connect";

    /// <summary>
    ///     Gets whether the primary button is shown at all. A paired
    ///     non-audio device has no on-demand connect (it reconnects itself when
    ///     used), so its only action is Remove — and an unpaired device Windows
    ///     says cannot be paired offers nothing at all rather than a Pair button
    ///     that is guaranteed to fail.
    /// </summary>
    public bool PrimaryActionVisible => Paired ? AudioConnectable : CanPair;

    /// <summary>Gets whether the Remove (unpair) button is shown.</summary>
    public bool RemoveVisible => Paired;

    /// <summary>
    ///     Gets whether this row is showing its actions. Same reasoning as
    ///     the Wi-Fi rows: a tap reveals the choice, it does not take it.
    /// </summary>
    public bool Expanded
    {
        get;
        internal set => SetFieldIfChanged(ref field, value, nameof(Expanded));
    }

    /// <summary>
    ///     Gets the icon state: accent only for a live connection, muted
    ///     for everything else — the same rule as the taskbar tile.
    /// </summary>
    public RadioIconState IconState => Connected
        ? RadioIconState.Connected
        : RadioIconState.Disconnected;

    /// <summary>Gets the second line under the name.</summary>
    public string StatusLine => Busy
        ? "Working..."
        : Connected
            ? "Connected"
            : Paired
                ? "Paired"
                : CanPair
                    ? "Available"
                    : "Not available";
}
