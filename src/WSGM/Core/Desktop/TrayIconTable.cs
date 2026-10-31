using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>How a tray request changed the icon table.</summary>
public enum TrayChange
{
    /// <summary>
    ///     The request was rejected (unknown icon, duplicate add, no hwnd) —
    ///     return 0 from WM_COPYDATA so shell32 reports failure and well-behaved apps
    ///     re-add.
    /// </summary>
    Rejected,

    /// <summary>A new icon appeared.</summary>
    Added,

    /// <summary>An existing icon changed.</summary>
    Updated,

    /// <summary>An icon was removed.</summary>
    Removed
}

/// <summary>
///     The live set of registered tray icons: identity resolution
///     ((hwnd,uid) pair, or GUID when NIF_GUID is set), per-flag field updates, and
///     version negotiation — Explorer's semantics, pure and unit-tested.
/// </summary>
public sealed class TrayIconTable
{
    private readonly List<TrayIcon> _icons = [];

    /// <summary>Gets every registered icon, including hidden ones, registration order.</summary>
    public IReadOnlyList<TrayIcon> Icons => _icons;

    /// <summary>Finds the icon a request refers to, or null.</summary>
    /// <param name="n">The parsed request.</param>
    private TrayIcon? Find(TrayProtocol.TrayNotification n)
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
    public TrayChange Apply(TrayProtocol.TrayNotification n, out TrayIcon? affected)
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

                var added = new TrayIcon(n.Hwnd, n.Uid, (n.Flags & TrayProtocol.NifGuid) != 0 ? n.Guid : Guid.Empty);
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

    private static void ApplyFields(TrayIcon icon, TrayProtocol.TrayNotification n)
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

    /// <summary>One registered tray icon.</summary>
    public sealed class TrayIcon
    {
        /// <summary>Creates table-owned registration state without retaining the sender's native icon handle.</summary>
        /// <param name="hwnd">Borrowed callback window identity.</param>
        /// <param name="uid">Application-assigned icon ID.</param>
        /// <param name="guid">Optional GUID identity, or Guid.Empty.</param>
        internal TrayIcon(nint hwnd, uint uid, Guid guid)
        {
            Hwnd = hwnd;
            Uid = uid;
            Guid = guid;
        }

        /// <summary>Gets the owning app's callback window.</summary>
        public nint Hwnd { get; }

        /// <summary>Gets the app-chosen icon id.</summary>
        public uint Uid { get; }

        /// <summary>Gets the GUID identity (Guid.Empty when the app didn't use NIF_GUID).</summary>
        public Guid Guid { get; internal set; }

        /// <summary>Gets the message to send the app for icon interactions.</summary>
        public uint CallbackMessage { get; internal set; }

        /// <summary>Gets the negotiated protocol version (0/3 legacy, 4 = coords protocol).</summary>
        public uint Version { get; internal set; }

        /// <summary>Gets the tooltip text.</summary>
        public string Tip { get; internal set; } = "";

        /// <summary>Gets whether the app asked the icon to be hidden (NIS_HIDDEN).</summary>
        public bool IsHidden { get; internal set; }

        /// <summary>
        ///     Gets or sets the host-rasterized icon image (opaque to this
        ///     pure table; the tray host stores an Avalonia bitmap here). Ownership is
        ///     per icon: the host disposes this image when the icon is removed and
        ///     again at teardown, so one image instance must never be shared between
        ///     two icons — a NIS_SHAREDICON registration has to rasterize its own from
        ///     the shared handle.
        /// </summary>
        public object? IconImage { get; set; }
    }
}
