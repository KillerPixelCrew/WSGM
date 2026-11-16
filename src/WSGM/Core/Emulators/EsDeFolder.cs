using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One ROM folder ES-DE shows for one system: a configured ROM source or one system folder inside it.</summary>
/// <param name="SourceId">The ROM source the folder belongs to.</param>
/// <param name="SourceName">The source's name, which names the folder's link when a system has several.</param>
/// <param name="SystemId">The canonical WSGM system.</param>
/// <param name="Path">The folder on its current mount, or empty while its storage is not connected.</param>
/// <param name="Extensions">The file types the source accepts for that system, with their dots.</param>
internal sealed record EsDeFolder(
    string SourceId,
    string SourceName,
    string SystemId,
    string Path,
    IReadOnlyList<string> Extensions);
