using System;

namespace WSGM.Input;

/// <summary>
///     Buttons WSGM can bind. The low 16 bits deliberately match XInput's
///     wButtons so the mapping is a straight cast; the high bits are what SDL reports
///     beyond XInput — analog triggers folded into buttons (any pad), plus the back
///     paddles, Steam and Quick Access buttons of Deck-class (real or emulated) pads.
/// </summary>
[Flags]
internal enum GamepadButtons : uint
{
    /// <summary>Up on the directional pad.</summary>
    DPadUp = 0x0001,

    /// <summary>Down on the directional pad.</summary>
    DPadDown = 0x0002,

    /// <summary>Left on the directional pad.</summary>
    DPadLeft = 0x0004,

    /// <summary>Right on the directional pad.</summary>
    DPadRight = 0x0008,

    /// <summary>The Menu or Start button.</summary>
    Start = 0x0010,

    /// <summary>The View, Back, or Select button.</summary>
    Back = 0x0020,

    /// <summary>Press on the left thumbstick.</summary>
    LeftThumb = 0x0040,

    /// <summary>Press on the right thumbstick.</summary>
    RightThumb = 0x0080,

    /// <summary>The left shoulder button.</summary>
    LeftShoulder = 0x0100,

    /// <summary>The right shoulder button.</summary>
    RightShoulder = 0x0200,

    /// <summary>The primary face button.</summary>
    A = 0x1000,

    /// <summary>The secondary face button.</summary>
    B = 0x2000,

    /// <summary>The left face button.</summary>
    X = 0x4000,

    /// <summary>The top face button.</summary>
    Y = 0x8000,

    // Beyond XInput's 16 bits. The triggers are synthesized from SDL's trigger
    // axes on any pad; only the rest need Deck-class hardware.
    /// <summary>The synthesized left analog trigger button.</summary>
    LeftTrigger = 0x0001_0000,

    /// <summary>The synthesized right analog trigger button.</summary>
    RightTrigger = 0x0002_0000,

    /// <summary>The upper-left rear paddle.</summary>
    L4 = 0x0004_0000,

    /// <summary>The upper-right rear paddle.</summary>
    R4 = 0x0008_0000,

    /// <summary>The lower-left rear paddle.</summary>
    L5 = 0x0010_0000,

    /// <summary>The lower-right rear paddle.</summary>
    R5 = 0x0020_0000,

    /// <summary>The Steam or guide button.</summary>
    Steam = 0x0040_0000,

    /// <summary>The Quick Access button.</summary>
    QuickAccess = 0x0080_0000,

    /// <summary>Press on the left touchpad.</summary>
    LeftPadPress = 0x0100_0000,

    /// <summary>Press on the right touchpad.</summary>
    RightPadPress = 0x0200_0000
}

