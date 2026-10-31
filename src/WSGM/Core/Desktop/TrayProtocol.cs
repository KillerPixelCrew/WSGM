using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WSGM.Core;

/// <summary>
///     Parses the undocumented-but-stable Shell_NotifyIcon wire format that
///     shell32 delivers to the Shell_TrayWnd window via WM_COPYDATA (dwData 1):
///     a TRAYNOTIFYDATA header (signature 0x34753423 + NIM_* message) followed by a
///     NOTIFYICONDATAW whose handle fields are 32-bit ON EVERY ARCHITECTURE — the
///     wire struct predates x64 and was never widened (verified against ReactOS
///     undocshell.h, LiteStep, ManagedShell, and zebar; widening the handles here
///     with sign/zero confusion silently corrupts every HWND and HICON).
///     Everything in this file is pure byte/state logic so the executable
///     specification lives in unit tests fed with captured blobs; the live window
///     and handle work stays in Shell\TrayHost.
/// </summary>
public static class TrayProtocol
{
    /// <summary>TRAYNOTIFYDATA signature (ReactOS: NI_NOTIFY_SIG).</summary>
    public const uint Signature = 0x34753423;

    /// <summary>COPYDATASTRUCT.dwData for tray notifications.</summary>
    public const int CopyDataTray = 1;

    /// <summary>COPYDATASTRUCT.dwData for SHAppBarMessage traffic (stubbed).</summary>
    public const int CopyDataAppBar = 0;

    /// <summary>
    ///     COPYDATASTRUCT.dwData for SHLoadInProc requests, which WSGM rejects because it
    ///     owns the corresponding system surfaces instead of hosting Explorer extensions.
    /// </summary>
    public const int CopyDataLoadInProc = 2;

    /// <summary>COPYDATASTRUCT.dwData for Shell_NotifyIconGetRect queries.</summary>
    public const int CopyDataIconRect = 3;

    // NIM_* messages.
    /// <summary>NIM_ADD.</summary>
    public const uint NimAdd = 0;

    /// <summary>NIM_MODIFY.</summary>
    public const uint NimModify = 1;

    /// <summary>NIM_DELETE.</summary>
    public const uint NimDelete = 2;

    /// <summary>NIM_SETFOCUS.</summary>
    public const uint NimSetFocus = 3;

    /// <summary>NIM_SETVERSION.</summary>
    public const uint NimSetVersion = 4;

    // NIF_* validity flags.
    /// <summary>NIF_MESSAGE: uCallbackMessage is valid.</summary>
    public const uint NifMessage = 0x01;

    /// <summary>NIF_ICON: hIcon is valid.</summary>
    public const uint NifIcon = 0x02;

    /// <summary>NIF_TIP: szTip is valid.</summary>
    public const uint NifTip = 0x04;

    /// <summary>NIF_STATE: dwState/dwStateMask are valid.</summary>
    public const uint NifState = 0x08;

    /// <summary>NIF_GUID: guidItem identifies the icon.</summary>
    public const uint NifGuid = 0x20;

    /// <summary>NIS_HIDDEN state bit.</summary>
    public const uint NisHidden = 0x01;

    /// <summary>NIS_SHAREDICON state bit (hIcon belongs to another registration).</summary>
    public const uint NisSharedIcon = 0x02;

    /// <summary>
    ///     WM_USER — the first message value Windows reserves for an
    ///     application's own use. NOTIFYICONDATA.uCallbackMessage is documented as
    ///     application-defined, and every real tray implementation allocates it from
    ///     this range or above: WinForms NotifyIcon uses WM_USER + 1024, Qt's
    ///     QSystemTrayIcon uses WM_APP + 101 (WM_APP is 0x8000), and apps wanting a
    ///     process-unique value call RegisterWindowMessage, which returns 0xC000..0xFFFF.
    /// </summary>
    private const uint WmUser = 0x0400;

    /// <summary>The largest window message value (message numbers are 16-bit).</summary>
    private const uint MaxWindowMessage = 0xFFFF;

    // Wire offsets. Header: signature 0, message 4, NOTIFYICONDATA32 at 8.
    private const int NidOffset = 8;
    private const int MinimumNidSize = 952; // v3 shape, ends after guidItem

    /// <summary>
    ///     Whether a registered callback is in Windows' application-defined message range.
    ///     This governs activation only: registration remains successful for compatibility with
    ///     shell32 retry behavior.
    /// </summary>
    /// <param name="callbackMessage">The registered uCallbackMessage value.</param>
    /// <returns>True for the application-defined WM_USER through 0xFFFF range.</returns>
    public static bool IsRelayableCallback(uint callbackMessage)
    {
        return callbackMessage is >= WmUser and <= MaxWindowMessage;
    }

    /// <summary>
    ///     Parses a WM_COPYDATA dwData=1 payload. Returns false (and logs
    ///     nothing — the caller decides) for payloads whose signature or size don't
    ///     match the known TRAYNOTIFYDATA shapes.
    /// </summary>
    /// <param name="payload">The raw COPYDATASTRUCT.lpData bytes.</param>
    /// <param name="notification">The parsed request on success.</param>
    /// <returns>True for a recognized complete wire payload; false leaves notification null.</returns>
    /// <remarks>Native handles in the result are borrowed from the sender and must not be retained as owned resources.</remarks>
    public static bool TryParse(ReadOnlySpan<byte> payload, out TrayNotification? notification)
    {
        notification = null;
        if (payload.Length < NidOffset + MinimumNidSize)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(payload) != Signature)
        {
            return false;
        }

        var message = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var nid = payload[NidOffset..];

        var cbSize = BinaryPrimitives.ReadUInt32LittleEndian(nid);
        // The wire NID is always the 32-bit layout; cbSize gates which trailing
        // fields exist (v3 = 952 ends at guidItem, v4 = 956 adds hBalloonIcon).
        // Read the known v3 region and accept any complete trailing extension.
        if (cbSize < MinimumNidSize || nid.Length < cbSize)
        {
            return false;
        }

        var hwnd = (nint)BinaryPrimitives.ReadUInt32LittleEndian(nid[4..]);
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(nid[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(nid[12..]);
        var callback = BinaryPrimitives.ReadUInt32LittleEndian(nid[16..]);
        var icon = (nint)BinaryPrimitives.ReadUInt32LittleEndian(nid[20..]);
        var tip = ReadFixedString(nid[24..280]);
        var state = BinaryPrimitives.ReadUInt32LittleEndian(nid[280..]);
        var stateMask = BinaryPrimitives.ReadUInt32LittleEndian(nid[284..]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(nid[800..]);
        var guid = new Guid(nid.Slice(936, 16));

        notification = new TrayNotification(
            message, hwnd, uid, flags, callback, icon, tip, state, stateMask, version, guid);
        return true;
    }

    private static string ReadFixedString(ReadOnlySpan<byte> utf16Bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(utf16Bytes);
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }
}
