using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A colour or image the user picks for a patch option.</summary>
/// <param name="Name">The component's label.</param>
/// <param name="Type"><c>color-picker</c> or <c>image-picker</c>.</param>
/// <param name="On">The patch option the component belongs to.</param>
/// <param name="Default">The default colour or image path.</param>
/// <param name="CssVariable">The variable the value is written to, with its <c>--</c>.</param>
/// <param name="Tabs">The windows the variable is set in.</param>
public sealed record ThemeComponentManifest(
    string Name,
    string Type,
    string On,
    string Default,
    string CssVariable,
    IReadOnlyList<string> Tabs);
