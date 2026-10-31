using System;

namespace WSGM.Core;

/// <summary>One registered tray icon.</summary>
public sealed class TrayIconRecord
{
    /// <summary>Creates table-owned registration state without retaining the sender's native icon handle.</summary>
    /// <param name="hwnd">Borrowed callback window identity.</param>
    /// <param name="uid">Application-assigned icon ID.</param>
    /// <param name="guid">Optional GUID identity, or Guid.Empty.</param>
    internal TrayIconRecord(nint hwnd, uint uid, Guid guid)
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
