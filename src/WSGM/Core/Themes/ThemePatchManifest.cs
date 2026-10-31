using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One patch: a named choice between sets of injects.</summary>
/// <param name="Name">The patch's label.</param>
/// <param name="Type"><c>dropdown</c>, <c>checkbox</c>, <c>slider</c> or <c>none</c>; anything else is a dropdown.</param>
/// <param name="Default">The option chosen until the user picks another.</param>
/// <param name="Options">The options in manifest order, each with what it injects.</param>
/// <param name="Components">The colours and images picked under the patch.</param>
public sealed record ThemePatchManifest(
    string Name,
    string Type,
    string Default,
    IReadOnlyList<KeyValuePair<string, IReadOnlyList<ThemeInjectManifest>>> Options,
    IReadOnlyList<ThemeComponentManifest> Components);
