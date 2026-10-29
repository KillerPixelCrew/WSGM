using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace WSGM.Core;

/// <summary>A complete shortcut read tied to the account observed on both sides of the read.</summary>
internal sealed class SteamShortcutSnapshot(uint accountId, IReadOnlyList<ExistingShortcut> shortcuts)
    : ReadOnlyCollection<ExistingShortcut>(shortcuts.ToArray())
{
    internal uint AccountId { get; } = accountId;
}
