using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     The live set of registered tray icons: identity resolution
///     ((hwnd,uid) pair, or GUID when NIF_GUID is set), per-flag field updates, and
///     version negotiation — Explorer's semantics, pure and unit-tested.
/// </summary>
public sealed class TrayIconTable
{
    private readonly List<TrayIconRecord> _icons = [];

    /// <summary>Gets every registered icon, including hidden ones, registration order.</summary>
    public IReadOnlyList<TrayIconRecord> Icons => _icons;

    /// <summary>Finds the icon a request refers to, or null.</summary>
    /// <param name="n">The parsed request.</param>
    private TrayIconRecord? Find(TrayNotification n)
    {
        var byGuid = (n.Flags & TrayProtocol.NifGuid) != 0 && n.Guid != Guid.Empty;
        return _icons.FirstOrDefault(icon =>
            (byGuid && icon.Guid != Guid.Empty && icon.Guid == n.Guid)
            || (icon.Hwnd == n.Hwnd && icon.Uid == n.Uid));
    }

    /// <summary>
    ///     Applies a parsed request with Explorer's semantics. The affected
    ///     icon (for Added/Updated/Removed) is returned via <paramref name="affected" />.
    /// </summary>
    /// <param name="n">The parsed request.</param>
    /// <param name="affected">The icon the request created, changed, or removed.</param>
    /// <returns>The registration change or rejection; ignored requests may leave affected null.</returns>
    public TrayChange Apply(TrayNotification n, out TrayIconRecord? affected)
    {
        affected = null;
        var existing = Find(n);
        switch (n.Message)
        {
            case TrayProtocol.NimAdd:
                if (existing is not null)
                {
                    // Explorer fails a duplicate NIM_ADD; the app should modify.
                    return TrayChange.Rejected;
                }

                // A callback hwnd is required to ever deliver clicks (ManagedShell
                // rejects hwnd-less adds even with a GUID).
                if (n.Hwnd == 0)
                {
                    return TrayChange.Rejected;
                }

                var added = new TrayIconRecord(n.Hwnd, n.Uid,
                    (n.Flags & TrayProtocol.NifGuid) != 0 ? n.Guid : Guid.Empty);
                ApplyFields(added, n);
                _icons.Add(added);
                affected = added;
                return TrayChange.Added;

            case TrayProtocol.NimModify:
                if (existing is null)
                {
                    // Rejecting makes shell32 report failure, prompting a re-add.
                    return TrayChange.Rejected;
                }

                ApplyFields(existing, n);
                affected = existing;
                return TrayChange.Updated;

            case TrayProtocol.NimDelete:
                if (existing is null)
                {
                    return TrayChange.Rejected;
                }

                _icons.Remove(existing);
                affected = existing;
                return TrayChange.Removed;

            case TrayProtocol.NimSetVersion:
                if (existing is null)
                {
                    return TrayChange.Rejected;
                }

                // Explorer accepts 0..4; anything else fails.
                if (n.Version > 4)
                {
                    return TrayChange.Rejected;
                }

                existing.Version = n.Version;
                affected = existing;
                return TrayChange.Updated;

            case TrayProtocol.NimSetFocus:
                // Focus return is a no-op for a bar that isn't a focus scope yet.
                return existing is null ? TrayChange.Rejected : TrayChange.Updated;

            default:
                return TrayChange.Rejected;
        }
    }

    /// <summary>Drops every icon (host teardown).</summary>
    public void Clear()
    {
        _icons.Clear();
    }

    private static void ApplyFields(TrayIconRecord icon, TrayNotification n)
    {
        if ((n.Flags & TrayProtocol.NifMessage) != 0)
        {
            icon.CallbackMessage = n.CallbackMessage;
        }

        if ((n.Flags & TrayProtocol.NifTip) != 0)
        {
            icon.Tip = n.Tip;
        }

        if ((n.Flags & TrayProtocol.NifGuid) != 0 && n.Guid != Guid.Empty)
        {
            icon.Guid = n.Guid;
        }

        if ((n.Flags & TrayProtocol.NifState) != 0 && (n.StateMask & TrayProtocol.NisHidden) != 0)
        {
            icon.IsHidden = (n.State & TrayProtocol.NisHidden) != 0;
        }
        // NIF_ICON handle bookkeeping is deliberately NOT the table's job: the host
        // must rasterize synchronously while the foreign handle is alive, and it
        // rasterizes per icon even for NIS_SHAREDICON, so the table has no reason to
        // retain the wire handle.
    }
}
