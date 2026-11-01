using System;

namespace WSGM.Core;

/// <summary>
///     One parsed Shell_NotifyIcon request. Handle fields are already
///     zero-extended from their 32-bit wire form.
/// </summary>
/// <param name="Message">The NIM_* request.</param>
/// <param name="Hwnd">The registering app's callback window.</param>
/// <param name="Uid">The app-chosen icon id (identity with <paramref name="Hwnd" />).</param>
/// <param name="Flags">NIF_* validity flags.</param>
/// <param name="CallbackMessage">The app's callback message (valid with NIF_MESSAGE).</param>
/// <param name="IconHandle">The 32-bit HICON value (valid with NIF_ICON).</param>
/// <param name="Tip">The tooltip text (valid with NIF_TIP).</param>
/// <param name="State">NIS_* bits (valid with NIF_STATE, masked by StateMask).</param>
/// <param name="StateMask">Which NIS_* bits the request changes.</param>
/// <param name="Version">The requested protocol version (meaningful for NIM_SETVERSION).</param>
/// <param name="Guid">The icon GUID (identity when NIF_GUID is set).</param>
public sealed record TrayNotification(
    uint Message,
    nint Hwnd,
    uint Uid,
    uint Flags,
    uint CallbackMessage,
    nint IconHandle,
    string Tip,
    uint State,
    uint StateMask,
    uint Version,
    Guid Guid);
