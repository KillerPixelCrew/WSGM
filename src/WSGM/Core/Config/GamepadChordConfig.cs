namespace WSGM.Core;

/// <summary>
///     Controller shortcut: a set of buttons pressed together, optionally held.
///     Modelled on Handheld Companion's chords — buttons accumulate until every button is
///     released, so they don't have to be pressed on the same frame.
/// </summary>
public sealed class GamepadChordConfig
{
    /// <summary>False = no controller shortcut.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bit mask of XInput buttons (see Input.GamepadButtons).</summary>
    public int Buttons { get; set; }

    /// <summary>True = must be held (~600 ms); false = a normal press.</summary>
    public bool Hold { get; set; }
}
