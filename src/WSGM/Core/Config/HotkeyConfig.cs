namespace WSGM.Core;

/// <summary>Describes the optional system-wide keyboard shortcut for quick access.</summary>
public sealed class HotkeyConfig
{
    /// <summary>False = no keyboard shortcut at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether Control is required.</summary>
    public bool Ctrl { get; set; } = true;

    /// <summary>Whether Alt is required.</summary>
    public bool Alt { get; set; } = true;

    /// <summary>Whether Shift is required.</summary>
    public bool Shift { get; set; }

    /// <summary>Whether either Windows key is required.</summary>
    public bool Win { get; set; }

    /// <summary>Win32 virtual-key code. Default VK_HOME (0x24). 0 = unset.</summary>
    public int VirtualKey { get; set; } = 0x24;
}
