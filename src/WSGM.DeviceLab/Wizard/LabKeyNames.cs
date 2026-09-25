namespace WSGM.DeviceLab.Wizard;

/// <summary>Readable names for Windows virtual-key codes, matching the names knowledge records use.</summary>
internal static class LabKeyNames
{
    /// <summary>Names one virtual-key code.</summary>
    /// <param name="key">Virtual-key code.</param>
    /// <returns>The name, or <c>VK_xx</c>.</returns>
    public static string Name(ushort key)
    {
        return key switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
            >= 0x60 and <= 0x69 => "NumPad" + (key - 0x60),
            0x08 => "Back",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Control",
            0x12 => "Alt",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "LWin",
            0x5C => "RWin",
            0x5D => "Apps",
            0x5F => "Sleep",
            0xA0 => "LShift",
            0xA1 => "RShift",
            0xA2 => "LControl",
            0xA3 => "RControl",
            0xA4 => "LAlt",
            0xA5 => "RAlt",
            0xA6 => "BrowserBack",
            0xA7 => "BrowserForward",
            0xA8 => "BrowserRefresh",
            0xAC => "BrowserHome",
            0xAD => "VolumeMute",
            0xAE => "VolumeDown",
            0xAF => "VolumeUp",
            0xB0 => "MediaNextTrack",
            0xB1 => "MediaPreviousTrack",
            0xB2 => "MediaStop",
            0xB3 => "MediaPlayPause",
            0xB4 => "LaunchMail",
            0xB5 => "LaunchMediaSelect",
            0xB6 => "LaunchApplication1",
            0xB7 => "LaunchApplication2",
            0xFF => "Reserved",
            _ => $"VK_{key:X2}"
        };
    }
}
