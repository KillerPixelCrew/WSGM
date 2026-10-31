using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A read-only inspection of a target directory for one tool.</summary>
/// <param name="Unmanaged">Identity-matched copies WSGM does not own.</param>
/// <param name="Proxies">Every offered proxy name and whether it can be used.</param>
/// <param name="Missing">Owned files that are gone.</param>
/// <param name="Modified">Owned binaries whose size or time changed.</param>
internal sealed record GameToolInspection(
    IReadOnlyList<GameToolUnmanagedCopy> Unmanaged,
    IReadOnlyList<GameToolProxyStatus> Proxies,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Modified);
