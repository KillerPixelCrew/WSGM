using WSGM.Controls;
using WSGM.Core;
using WifiNetworkKey = WindowsDeviceControl.WindowsRadio.WifiNetworkKey;
using WifiSecurity = WindowsDeviceControl.WindowsRadio.WifiSecurity;

namespace WSGM.Shell;

/// <summary>
///     One row in the Wi-Fi list. A row instance survives refreshes so the
///     gamepad cursor keeps its place; only its values are updated.
/// </summary>
public sealed class WifiNetworkEntry : ObservableObject
{
    /// <summary>Creates a row for a network.</summary>
    /// <param name="key">The network's SSID bytes and security, which identify the row.</param>
    public WifiNetworkEntry(WifiNetworkKey key)
    {
        Key = key;
        Ssid = key.DisplayText;
    }

    /// <summary>Gets the network's identity. Immutable: two networks can show the same name.</summary>
    public WifiNetworkKey Key { get; }

    /// <summary>Gets the network name, for display.</summary>
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
