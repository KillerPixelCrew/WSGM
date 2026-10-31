using System.Collections.Generic;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One location on the Storage page: a card library, a disk or a drive, and its shortcuts.</summary>
/// <param name="Name">The location's name.</param>
/// <param name="Library">The card library this group is, or null for a disk or drive.</param>
/// <param name="Shortcuts">Its shortcuts, by name.</param>
internal sealed record StorageGroup(string Name, GameLibraryStorage? Library, IReadOnlyList<ShortcutStatus> Shortcuts);
