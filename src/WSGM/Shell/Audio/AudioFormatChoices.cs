using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The default output's format and spatial-sound choices, as the Steam rows read them.</summary>
/// <param name="Available">Whether any choice can be offered for the current output.</param>
/// <param name="Channels">Channel layouts, each id a full format.</param>
/// <param name="CurrentChannels">The format id in force, or empty.</param>
/// <param name="Formats">Sample formats for the current channel layout, each id a full format.</param>
/// <param name="CurrentFormat">The format id in force, or empty.</param>
/// <param name="Spatial">Spatial sound formats, Off first, each id a GUID.</param>
/// <param name="CurrentSpatial">The spatial format id in force, or empty.</param>
/// <param name="StatusText">One line of detail, or empty.</param>
internal sealed record AudioFormatChoices(
    bool Available,
    IReadOnlyList<ChoiceRowOption> Channels,
    string CurrentChannels,
    IReadOnlyList<ChoiceRowOption> Formats,
    string CurrentFormat,
    IReadOnlyList<ChoiceRowOption> Spatial,
    string CurrentSpatial,
    string StatusText);
