using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WSGM.AllyXLab;

/// <summary>What HidHide reports about itself and its allowed applications.</summary>
/// <param name="Available">Whether the control device could be opened at all.</param>
/// <param name="Active">Whether hiding is switched on.</param>
/// <param name="Inverse">Whether the list means "only these", which reverses its meaning.</param>
/// <param name="Applications">The allowed applications exactly as HidHide stored them.</param>
/// <param name="Detail">Why a read failed, when it did.</param>
internal sealed record HidHideState(bool Available, bool Active, bool Inverse, IReadOnlyList<string> Applications, string Detail);

/// <summary>
/// Reads HidHide and, with the tester's consent, adds this tool to its allowed applications for the
/// session. While HidHide hides a controller and this tool is not allowed, the capture sees no
/// gamepad at all, or only the virtual pad another manager created.
/// </summary>
/// <remarks>
/// The control codes and the two path notations follow WSGM's reviewed implementation in
/// <c>src/WSGM/Interop/NativeHidHide.cs</c> and <c>src/WSGM/Shell/HidHideOwnership.cs</c>. The
/// list is read again just before each write, and restoring removes only the entry this tool
/// added, so an entry someone else adds meanwhile (the HidHide Configuration Client, say) is kept.
/// Nothing else in HidHide's configuration is touched, and the hiding switch is never flipped.
/// </remarks>
internal static class HidHideAccess
{
    private const uint GetApplications = 0x80016000, SetApplications = 0x80016004, GetActive = 0x80016010, GetInverse = 0x80016018;

    internal static HidHideState Read(SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        using SafeFileHandle handle = Hid.CreateFile(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            var absent = new HidHideState(false, false, false, [], $"HidHide control device unavailable ({error}).");
            log.Add("hidhide-state", absent);
            return absent;
        }

        try
        {
            HidHideState state = new(true, ReadFlag(handle, GetActive), ReadFlag(handle, GetInverse), ReadList(handle, GetApplications), "");
            log.Add("hidhide-state", state);
            return state;
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            var failed = new HidHideState(true, false, false, [], e.Message);
            log.Add("hidhide-state", failed);
            return failed;
        }
    }

    /// <summary>Adds this executable to HidHide's allowed applications.</summary>
    /// <param name="log">The session log.</param>
    /// <returns>The entry that was added, so it can be removed again, or null when nothing was
    /// written.</returns>
    /// <remarks>The list is read here, not taken from the read before the consent prompt, so the
    /// write carries whatever changed while the tester decided.</remarks>
    internal static string? TryAllow(SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("This tool has no image path.");
        HidHideState state = Read(log);
        if (!state.Available || !string.IsNullOrEmpty(state.Detail) || state.Inverse || Contains(state.Applications, self))
        {
            log.Add("hidhide-allow-skipped", new
            {
                Reason = !state.Available || !string.IsNullOrEmpty(state.Detail) ? "control device unavailable"
                    : state.Inverse ? "inverse mode: the list denies, so adding this tool would hide devices from it"
                    : "already allowed",
            });
            return null;
        }

        List<string> updated = [.. state.Applications, self];
        Write(updated, log);
        log.Add("hidhide-allow", new { Added = self, Previous = state.Applications.Count, Now = updated.Count });
        return self;
    }

    /// <summary>Removes the entry this tool added, leaving every other entry as it is now.</summary>
    /// <param name="added">The entry <see cref="TryAllow"/> returned.</param>
    /// <param name="log">The session log.</param>
    /// <returns>Whether the list read back without that entry.</returns>
    internal static bool Restore(string added, SessionLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(added);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            HidHideState current = Read(log);
            if (!current.Available || !string.IsNullOrEmpty(current.Detail))
            {
                log.Add("hidhide-restore", new { Restored = false, Reason = "control device unavailable" });
                return false;
            }

            List<string> remaining = [.. current.Applications.Where(entry => !IsEntry(entry, added))];
            if (remaining.Count != current.Applications.Count)
            {
                Write(remaining, log);
            }

            HidHideState after = Read(log);
            bool restored = after.Available && string.IsNullOrEmpty(after.Detail)
                && !after.Applications.Any(entry => IsEntry(entry, added));
            log.Add("hidhide-restore", new { Restored = restored, Removed = current.Applications.Count - remaining.Count, Entries = after.Applications.Count });
            return restored;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            log.Add("hidhide-restore", new { Restored = false, e.Message });
            return false;
        }
    }

    /// <summary>Whether HidHide already lists this application, in either notation.</summary>
    /// <param name="entries">Entries as HidHide returned them.</param>
    /// <param name="value">The application path to look for.</param>
    /// <returns>Whether it is present.</returns>
    internal static bool Contains(IEnumerable<string> entries, string value)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string normalized = NormalizePath(value);
        return entries.Any(entry => string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizePath(entry), normalized, StringComparison.OrdinalIgnoreCase));
    }

    // Only the exact string this tool wrote is its own; a matching entry in the other notation was
    // put there by someone else.
    private static bool IsEntry(string entry, string added) => string.Equals(entry, added, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reduces a path to the form both notations agree on.</summary>
    /// <param name="value">A drive-letter path or an NT device path.</param>
    /// <returns>The comparable form.</returns>
    internal static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string path = value.Trim().Replace('/', '\\');
        const string devicePrefix = @"\device\harddiskvolume";
        if (path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            int separator = path.IndexOf('\\', devicePrefix.Length);
            return separator < 0 ? string.Empty : path[separator..];
        }

        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return path.Length == 2 ? string.Empty : path[2..];
        }

        return path;
    }

    private static void Write(IReadOnlyList<string> entries, SessionLog log)
    {
        using SafeFileHandle handle = Hid.CreateFile(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide control device unavailable for writing.");
        }

        byte[] buffer = Encode(entries);
        log.Add("hidhide-write-attempt", new { Entries = entries.Count, Bytes = buffer.Length });
        if (!DeviceIoControl(handle, SetApplications, buffer, (uint)buffer.Length, null, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide refused the allowed-application list; nothing was changed.");
        }
    }

    private static bool ReadFlag(SafeFileHandle handle, uint code)
    {
        byte[] raw = new byte[1];
        return DeviceIoControl(handle, code, null, 0, raw, 1, out uint returned, IntPtr.Zero) && returned == 1 && raw[0] != 0;
    }

    private static IReadOnlyList<string> ReadList(SafeFileHandle handle, uint code)
    {
        for (int size = 4096; size <= 1024 * 1024; size *= 2)
        {
            byte[] buffer = new byte[size];
            if (DeviceIoControl(handle, code, null, 0, buffer, (uint)buffer.Length, out uint returned, IntPtr.Zero))
            {
                return returned > buffer.Length || (returned & 1) != 0 ? [] : Decode(buffer.AsSpan(0, (int)returned));
            }

            int error = Marshal.GetLastWin32Error();
            if (error is not 122 and not 234)
            {
                throw new Win32Exception(error, "HidHide list read failed.");
            }
        }

        throw new IOException("HidHide list exceeds the bound this tool reads.");
    }

    private static List<string> Decode(ReadOnlySpan<byte> bytes)
    {
        List<string> values = [];
        foreach (string entry in Encoding.Unicode.GetString(bytes).Split('\0'))
        {
            if (entry.Length == 0)
            {
                break;
            }

            values.Add(entry);
        }

        return values;
    }

    private static byte[] Encode(IReadOnlyList<string> values)
    {
        StringBuilder builder = new();
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            {
                throw new InvalidOperationException("A HidHide entry is empty or contains a NUL.");
            }

            builder.Append(value).Append('\0');
        }

        builder.Append('\0');
        if (values.Count == 0)
        {
            builder.Append('\0');
        }

        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputBytes, byte[]? output, uint outputBytes, out uint returned, IntPtr overlapped);
}
