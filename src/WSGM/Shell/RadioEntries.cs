using System.ComponentModel;

namespace WSGM.Shell;

/// <summary>How a Wi-Fi network protects itself, mirroring the helper's codes.</summary>
public enum WifiSecurity
{
    /// <summary>No authentication; joining needs no password.</summary>
    Open = 0,

    /// <summary>A pre-shared key. Joining asks for a password.</summary>
    Personal = 1,

    /// <summary>802.1X. Not supported here — it needs an EAP configuration and a
    /// credential flow a game-mode panel has no business guessing at.</summary>
    Enterprise = 2,
}

/// <summary>The power state of a radio.</summary>
public enum RadioPower
{
    /// <summary>The radio is on.</summary>
    On = 0,

    /// <summary>The radio is off but switchable.</summary>
    Off = 1,

    /// <summary>Disabled by policy or a hardware switch; not switchable.</summary>
    Disabled = 2,

    /// <summary>State could not be read.</summary>
    Unknown = 3,

    /// <summary>No radio of this kind exists on this machine.</summary>
    Absent = 4,
}

/// <summary>One row in the Wi-Fi list. A row instance survives refreshes so the
/// gamepad cursor keeps its place; only its values are updated.</summary>
public sealed class WifiNetworkEntry : INotifyPropertyChanged
{
    /// <summary>Raised after a displayed value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a row for a network.</summary>
    /// <param name="ssid">The network name, which also identifies the row.</param>
    public WifiNetworkEntry(string ssid) => Ssid = ssid;

    /// <summary>Gets the network name. Immutable: it is the row's identity.</summary>
    public string Ssid { get; }

    private int _signal;
    /// <summary>Gets the signal quality, 0-100.</summary>
    public int Signal
    {
        get => _signal;
        internal set => Set(ref _signal, value, nameof(Signal));
    }

    private WifiSecurity _security;
    /// <summary>Gets how the network is protected.</summary>
    public WifiSecurity Security
    {
        get => _security;
        internal set
        {
            if (_security != value)
            {
                _security = value;
                Raise(nameof(Security));
                Raise(nameof(NeedsPassword));
            }
        }
    }

    private bool _saved;
    /// <summary>Gets whether a saved profile exists, so joining needs no password.</summary>
    public bool Saved
    {
        get => _saved;
        internal set
        {
            if (_saved != value)
            {
                _saved = value;
                Raise(nameof(Saved));
                Raise(nameof(NeedsPassword));
            }
        }
    }

    private bool _connected;
    /// <summary>Gets whether this is the network currently joined.</summary>
    public bool Connected
    {
        get => _connected;
        internal set => Set(ref _connected, value, nameof(Connected));
    }

    /// <summary>Gets whether joining this network needs a password prompt: it is
    /// secured, and no saved profile already carries the key.</summary>
    public bool NeedsPassword => Security == WifiSecurity.Personal && !Saved;

    private void Set(ref int field, int value, string name)
    {
        if (field != value)
        {
            field = value;
            Raise(name);
        }
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (field != value)
        {
            field = value;
            Raise(name);
        }
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One row in the Bluetooth list. Same in-place refresh discipline as
/// <see cref="WifiNetworkEntry"/>.</summary>
public sealed class BluetoothDeviceEntry : INotifyPropertyChanged
{
    /// <summary>Raised after a displayed value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a row for a device.</summary>
    /// <param name="id">The WinRT device id, which identifies the row.</param>
    public BluetoothDeviceEntry(string id) => Id = id;

    /// <summary>Gets the WinRT device id. Immutable: it is the row's identity.</summary>
    public string Id { get; }

    private string _name = "";
    /// <summary>Gets the display name, or a placeholder when the device has not
    /// advertised one yet.</summary>
    public string Name
    {
        get => _name.Length == 0 ? "Unnamed device" : _name;
        internal set
        {
            if (_name != value)
            {
                _name = value;
                Raise(nameof(Name));
            }
        }
    }

    private bool _paired;
    /// <summary>Gets whether the device is paired.</summary>
    public bool Paired
    {
        get => _paired;
        internal set
        {
            if (_paired != value)
            {
                _paired = value;
                Raise(nameof(Paired));
                Raise(nameof(ActionText));
            }
        }
    }

    private bool _canPair;
    /// <summary>Gets whether Windows believes pairing is currently possible.</summary>
    public bool CanPair
    {
        get => _canPair;
        internal set => Set(ref _canPair, value, nameof(CanPair));
    }

    private bool _busy;
    /// <summary>Gets whether an operation is in flight for this device.</summary>
    public bool Busy
    {
        get => _busy;
        internal set
        {
            if (_busy != value)
            {
                _busy = value;
                Raise(nameof(Busy));
                Raise(nameof(ActionText));
            }
        }
    }

    /// <summary>Gets the label for this row's button.</summary>
    public string ActionText => Busy ? "Working..." : Paired ? "Remove" : "Pair";

    private void Set(ref bool field, bool value, string name)
    {
        if (field != value)
        {
            field = value;
            Raise(name);
        }
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
