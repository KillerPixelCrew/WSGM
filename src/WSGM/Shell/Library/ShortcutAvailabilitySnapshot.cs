using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One availability pass's published result.</summary>
/// <param name="Revision">Monotonic pass revision; unchanged passes keep the previous one.</param>
/// <param name="Checked">Whether a pass has read Steam's shortcuts at least once.</param>
/// <param name="Shortcuts">Every tracked non-Steam shortcut, ordered by location and name.</param>
/// <param name="Libraries">Every known or connected card library, with its immediate actions.</param>
/// <param name="Volumes">Every mounted local volume, identified through its GUID path.</param>
/// <param name="Cards">Connected card libraries by library id, excluding ambiguous ones.</param>
/// <param name="Ambiguous">Library ids that more than one connected volume claims.</param>
internal sealed record ShortcutAvailabilitySnapshot(
    long Revision,
    bool Checked,
    IReadOnlyList<ShortcutStatus> Shortcuts,
    IReadOnlyList<GameLibraryStorage> Libraries,
    IReadOnlyList<ObservedVolume> Volumes,
    IReadOnlyDictionary<string, ConnectedCard> Cards,
    IReadOnlySet<string> Ambiguous)
{
    /// <summary>Before the first pass.</summary>
    internal static ShortcutAvailabilitySnapshot Empty { get; } = new(0, false, [], [], [],
        new Dictionary<string, ConnectedCard>(), new HashSet<string>());

    /// <summary>The latest status of one shortcut, or null when it is not tracked or not checked yet.</summary>
    internal ShortcutStatus? Find(uint appId)
    {
        return appId == 0 ? null : Shortcuts.FirstOrDefault(status => status.AppId == appId);
    }
}
