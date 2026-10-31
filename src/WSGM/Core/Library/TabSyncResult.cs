using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
///     Outcome of a tab sync: whether the injection succeeded, and the native
///     Steam tabs the resident script observed in the strip (empty when the library has
///     not rendered yet — callers must treat that as "unknown", not "none").
/// </summary>
/// <param name="Ok">Whether the definitions reached Steam without a script error.</param>
/// <param name="NativeTabs">Native tabs observed in Steam's tab array.</param>
public sealed record TabSyncResult(bool Ok, List<NativeTabConfig> NativeTabs);
