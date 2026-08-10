using System.ComponentModel;

namespace WSGM.Shell;

/// <summary>How a listed drive gets ejected safely.</summary>
public enum EjectKind
{
    /// <summary>A hot-pluggable device (USB stick, USB HDD/SSD): PnP device
    /// eject, which removes every volume of the device at once.</summary>
    UsbDevice = 0,

    /// <summary>Removable media in a non-removable device (microSD in a built-in
    /// reader): media-level dismount and eject. A device-level eject here would
    /// disable the reader itself until reboot.</summary>
    Media = 1,
}

/// <summary>One row in the Safe Eject list — a physical removable device (all of
/// its volumes together), or one piece of removable media. A row instance
/// survives refreshes so the gamepad cursor keeps its place; only its values are
/// updated (the radio/Bluetooth row discipline).</summary>
public sealed class RemovableDriveEntry : INotifyPropertyChanged
{
    /// <summary>Raised after a displayed value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a row.</summary>
    /// <param name="id">The device instance path (or "media:X" for a media row),
    /// which identifies the row across refreshes.</param>
    /// <param name="kind">How this row ejects.</param>
    public RemovableDriveEntry(string id, EjectKind kind)
    {
        Id = id;
        Kind = kind;
    }

    /// <summary>Gets the row's identity. Immutable.</summary>
    public string Id { get; }

    /// <summary>Gets how this row ejects. Immutable: a reclassified device gets
    /// a new id and therefore a new row.</summary>
    public EjectKind Kind { get; }

    /// <summary>The devnode the PnP eject targets (USB-device rows). Refreshed
    /// with every snapshot; read by the manager, not the UI.</summary>
    internal uint DevInst { get; set; }

    /// <summary>The drive letter the media-level eject opens (media rows).</summary>
    internal char VolumeLetter { get; set; }

    private string _name = "";
    /// <summary>Gets the device's display name, or a placeholder when the
    /// hardware did not offer one.</summary>
    public string Name
    {
        get => _name.Length == 0 ? "Removable drive" : _name;
        internal set
        {
            if (_name != value)
            {
                _name = value;
                Raise(nameof(Name));
            }
        }
    }

    private string _letters = "";
    /// <summary>Gets the drive letter(s), e.g. "E:" or "E:, F:".</summary>
    public string Letters
    {
        get => _letters;
        internal set
        {
            if (_letters != value)
            {
                _letters = value;
                Raise(nameof(Letters));
                Raise(nameof(StatusLine));
            }
        }
    }

    private string _sizeText = "";
    /// <summary>Gets the total capacity as display text, e.g. "512 GB".</summary>
    public string SizeText
    {
        get => _sizeText;
        internal set
        {
            if (_sizeText != value)
            {
                _sizeText = value;
                Raise(nameof(StatusLine));
            }
        }
    }

    private bool _busy;
    /// <summary>Gets whether an eject is in flight for this row.</summary>
    public bool Busy
    {
        get => _busy;
        internal set
        {
            if (_busy != value)
            {
                _busy = value;
                Raise(nameof(Busy));
                Raise(nameof(StatusLine));
                Raise(nameof(ActionEnabled));
            }
        }
    }

    private bool _ejected;
    /// <summary>Gets whether this row's eject already succeeded, so the hardware
    /// is safe to pull. The row usually disappears on the next refresh; until it
    /// does, its button must not offer a second eject.</summary>
    public bool Ejected
    {
        get => _ejected;
        internal set
        {
            if (_ejected != value)
            {
                _ejected = value;
                Raise(nameof(Ejected));
                Raise(nameof(StatusLine));
                Raise(nameof(ActionEnabled));
            }
        }
    }

    private string _resultText = "";
    /// <summary>Gets the last eject outcome for this row ("Safe to remove", or a
    /// veto message). Cleared when a fresh snapshot shows the drive back in
    /// ordinary use.</summary>
    public string ResultText
    {
        get => _resultText;
        internal set
        {
            if (_resultText != value)
            {
                _resultText = value;
                Raise(nameof(StatusLine));
            }
        }
    }

    private bool _expanded;
    /// <summary>Gets whether this row is showing its Eject button. Selecting
    /// reveals the action rather than taking it — a stray tap must never rip a
    /// game library out from under a running session.</summary>
    public bool Expanded
    {
        get => _expanded;
        internal set
        {
            if (_expanded != value)
            {
                _expanded = value;
                Raise(nameof(Expanded));
            }
        }
    }

    /// <summary>Gets whether the Eject button may be pressed.</summary>
    public bool ActionEnabled => !Busy && !Ejected;

    /// <summary>Gets the second line under the name: the in-flight/outcome state
    /// when there is one, else the letters and capacity.</summary>
    public string StatusLine => Busy
        ? "Ejecting..."
        : ResultText.Length > 0
        ? ResultText
        : SizeText.Length > 0
        ? $"{Letters} — {SizeText}"
        : Letters;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
