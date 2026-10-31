using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What the UI shows of a patch.</summary>
/// <param name="Name">Its label.</param>
/// <param name="Default">The default option.</param>
/// <param name="Value">The chosen option.</param>
/// <param name="Options">The options in order.</param>
/// <param name="Type"><c>dropdown</c>, <c>checkbox</c>, <c>slider</c> or <c>none</c>.</param>
/// <param name="Components">The colours and images under it.</param>
public sealed record ThemePatchSnapshot(
    string Name,
    string Default,
    string Value,
    IReadOnlyList<string> Options,
    string Type,
    IReadOnlyList<ThemeComponentSnapshot> Components);
