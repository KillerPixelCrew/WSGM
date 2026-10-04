using System;
using System.Collections.Generic;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    private GamepadChordConfig _chord = new();
    private bool _chordRecording;

    // --- Gestures / glyphs ---
    private int _glyphStyleIndex;

    // --- Overlay shortcuts (recorded, not picked from a list) ---
    private HotkeyConfig _hotkey = new();

    private bool _hotkeyRecording;

    /// <summary>
    ///     Gets or sets whether WSGM leases the controller away from Steam
    ///     Input while its focused surfaces are open. Off = Steam is never touched.
    /// </summary>
    public bool SteamInputLeaseEnabled
    {
        get;
        set => SetField(ref field, value, nameof(SteamInputLeaseEnabled));
    }

    /// <summary>Gets the current keyboard shortcut or the key-recording prompt.</summary>
    public string HotkeyText => _hotkeyRecording ? "Press keys…" : KeyRecorder.Describe(_hotkey);

    /// <summary>Gets the current controller chord or the button-recording prompt.</summary>
    public string ChordText => _chordRecording
        ? "Press buttons…"
        : _chord.Enabled && _chord.Buttons != 0
            ? GamepadService.Describe((GamepadButtons)_chord.Buttons, _chord.Hold)
            : "None";

    /// <summary>Gets or sets whether a bottom-edge swipe opens quick access on its Open apps strip (game mode).</summary>
    public bool GestureBottom
    {
        get;
        set => SetField(ref field, value, nameof(GestureBottom));
    }

    /// <summary>Gets or sets whether a top-edge swipe opens quick access.</summary>
    public bool GestureTop
    {
        get;
        set => SetField(ref field, value, nameof(GestureTop));
    }

    /// <summary>Gets or sets whether a left-edge swipe opens Steam's Big Picture menu.</summary>
    public bool GestureLeftSteamMenu
    {
        get;
        set => SetField(ref field, value, nameof(GestureLeftSteamMenu));
    }

    /// <summary>Gets or sets whether a right-edge swipe opens Steam's Big Picture quick-access menu.</summary>
    public bool GestureRightSteamQuickAccess
    {
        get;
        set => SetField(ref field, value, nameof(GestureRightSteamQuickAccess));
    }

    /// <summary>Gets or sets the selected controller-glyph family index.</summary>
    public int GlyphStyleIndex
    {
        get => _glyphStyleIndex;
        set
        {
            _glyphStyleIndex = value;
            Raise(nameof(GlyphStyleIndex));
            Raise(nameof(GlyphStyle));
        }
    }

    /// <summary>
    ///     Gets the selected glyph family as its enum value — what the
    ///     status strip's A/B glyph icons bind to.
    /// </summary>
    public GlyphStyle GlyphStyle => (GlyphStyle)Math.Clamp(_glyphStyleIndex, 0, 2);

    /// <summary>Gets the controller-glyph family names presented by the settings selector.</summary>
    public List<string> GlyphStyles { get; } = ["Xbox", "PlayStation", "Nintendo"];

    /// <summary>Gaussian blur of the Overlay background in physical pixels.</summary>
    public double OverlayBlurRadius
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(OverlayBlurRadius)))
            {
                Raise(nameof(OverlayBlurLabel));
            }
        }
    } = 8;

    /// <summary>Text beside the Overlay blur slider.</summary>
    public string OverlayBlurLabel => $"{OverlayBlurRadius:0} px";

    /// <summary>Starts or stops keyboard-shortcut recording.</summary>
    /// <param name="recording">Whether the next eligible key combination should be captured.</param>
    public void SetHotkeyRecording(bool recording)
    {
        _hotkeyRecording = recording;
        Raise(nameof(HotkeyText));
    }

    /// <summary>Starts or stops controller-chord recording.</summary>
    /// <param name="recording">Whether the next eligible controller chord should be captured.</param>
    public void SetChordRecording(bool recording)
    {
        _chordRecording = recording;
        Raise(nameof(ChordText));
    }

    /// <summary>Stores a recorded keyboard shortcut, already in configuration shape.</summary>
    /// <param name="hotkey">The captured shortcut, or null to retain the current binding.</param>
    public void ApplyRecordedHotkey(HotkeyConfig? hotkey)
    {
        if (hotkey is not null)
        {
            _hotkey = hotkey;
        }

        SetHotkeyRecording(false);
    }

    /// <summary>Stores a controller chord; no buttons clears it, which only the Clear button sends.</summary>
    /// <param name="buttons">The buttons captured from one controller.</param>
    /// <param name="hold">Whether the chord activates on a hold rather than an edge.</param>
    internal void ApplyRecordedChord(GamepadButtons buttons, bool hold)
    {
        _chord = new GamepadChordConfig
        {
            Enabled = buttons != 0,
            Buttons = (int)buttons,
            Hold = hold
        };
        SetChordRecording(false);
    }

    /// <summary>Clears the keyboard shortcut.</summary>
    public void ClearHotkey()
    {
        ApplyRecordedHotkey(KeyRecorder.Cleared());
    }

    /// <summary>Clears the controller chord.</summary>
    public void ClearChord()
    {
        ApplyRecordedChord(0, false);
    }
}
