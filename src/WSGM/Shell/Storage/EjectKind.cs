namespace WSGM.Shell;

/// <summary>How a listed drive gets ejected safely.</summary>
public enum EjectKind
{
    /// <summary>
    ///     A hot-pluggable device (USB stick, USB HDD/SSD): PnP device
    ///     eject, which removes every volume of the device at once.
    /// </summary>
    UsbDevice = 0,

    /// <summary>
    ///     Removable media in a non-removable device (microSD in a built-in
    ///     reader): media-level dismount and eject. A device-level eject here would
    ///     disable the reader itself until reboot.
    /// </summary>
    Media = 1
}
