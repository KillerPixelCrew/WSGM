using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One entry of an <c>inject</c> map: a stylesheet, or a CSS variable, and the windows it is for.</summary>
/// <param name="Key">
///     The stylesheet's path relative to the theme folder, or a variable name starting with <c>--</c>,
///     in which case the first tab is the variable's value.
/// </param>
/// <param name="Tabs">The tab names or aliases, expanded by <see cref="ThemeTargets" />.</param>
public sealed record ThemeInjectManifest(string Key, IReadOnlyList<string> Tabs);
