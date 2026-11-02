using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The device's performance presets and their plugged-in and battery assignments.</summary>
/// <param name="Available">Whether presets can be assigned now.</param>
/// <param name="Options">The presets; Custom is listed unselectable while an assignment holds it.</param>
/// <param name="Current">The name of the preset in effect, Custom, or Unavailable.</param>
/// <param name="StatusText">One line of detail, or empty.</param>
/// <param name="Ac">The plugged-in assignment's preset id, or empty for none.</param>
/// <param name="Battery">The battery assignment's preset id, or empty for none.</param>
/// <param name="Scope">What the assignments edit, or empty.</param>
/// <param name="UnsetLabel">What the empty assignment is called.</param>
/// <param name="AcAccent">Whether the running game's profile supplies the plugged-in assignment.</param>
/// <param name="BatteryAccent">Whether the running game's profile supplies the battery assignment.</param>
/// <param name="CanSave">Whether the current Custom reading can be saved as a preset, or used to update one.</param>
/// <param name="SaveName">The suggested name for a new preset, unique for this device.</param>
/// <param name="CurrentValues">The current reading in one line, or empty when it is unknown.</param>
/// <param name="Saved">The user's saved presets for this device, in saved order.</param>
internal sealed record PowerPresetRowState(
    bool Available,
    IReadOnlyList<ChoiceRowOption> Options,
    string Current,
    string StatusText,
    string Ac,
    string Battery,
    string Scope,
    string UnsetLabel,
    bool AcAccent = false,
    bool BatteryAccent = false,
    bool CanSave = false,
    string SaveName = "",
    string CurrentValues = "",
    IReadOnlyList<PowerPresetSavedRow>? Saved = null);
