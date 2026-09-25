using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WSGM.Interop;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What HidHide reports about itself.</summary>
/// <param name="Available">Whether the control device could be opened.</param>
/// <param name="Active">Whether hiding is switched on.</param>
/// <param name="Inverse">Whether the list means "only these", which reverses its meaning.</param>
/// <param name="Applications">Allowed applications exactly as HidHide stored them.</param>
/// <param name="Detail">Why a read failed, when it did.</param>
internal sealed record HidHideState(
    bool Available,
    bool Active,
    bool Inverse,
    IReadOnlyList<string> Applications,
    string? Detail);

/// <summary>What <see cref="HidHideAllowance.TryAllow" /> did.</summary>
/// <param name="Added">The entry written, or null when nothing was written.</param>
/// <param name="Reason">Why nothing was written, when it was not.</param>
internal sealed record HidHideAllowResult(string? Added, string? Reason);

/// <summary>The HidHide control device operations the allowance needs.</summary>
internal interface IHidHideDevice
{
    /// <summary>Reads HidHide's state.</summary>
    /// <returns>The state.</returns>
    HidHideState Read();

    /// <summary>Replaces the allowed-application list.</summary>
    /// <param name="applications">The new list.</param>
    /// <returns>Null on success, or the error.</returns>
    string? WriteApplications(IReadOnlyList<string> applications);
}

/// <summary>
///     Adds the lab to HidHide's allowed applications for the session and removes exactly that entry
///     again. While HidHide hides a controller and the lab is not allowed, capture sees no gamepad at all,
///     or only a virtual one.
/// </summary>
/// <remarks>
///     Ported from AllyXLab 0.3.2 onto WSGM's <see cref="NativeHidHide" />. The list is read again just
///     before each write; restoring removes only the exact string the lab wrote, so an entry someone else
///     adds meanwhile is kept. The hiding switch and an inverse-mode list are never touched. The entry is
///     recorded in <see cref="LabMachineState" /> before it is written.
/// </remarks>
internal sealed class HidHideAllowance(IHidHideDevice device, LabMachineState state, string selfPath)
{
    /// <summary>Allowance against the real HidHide driver for this executable.</summary>
    /// <param name="state">Where the entry is recorded.</param>
    /// <param name="selfPath">This executable's path.</param>
    /// <returns>The allowance.</returns>
    public static HidHideAllowance ForMachine(LabMachineState state, string selfPath)
    {
        return new HidHideAllowance(new NativeHidHideDevice(), state, selfPath);
    }

    /// <summary>Reads HidHide's state.</summary>
    /// <returns>The state.</returns>
    public HidHideState Read()
    {
        return device.Read();
    }

    /// <summary>Adds this executable to the allowed applications.</summary>
    /// <returns>What was written.</returns>
    public HidHideAllowResult TryAllow()
    {
        var current = device.Read();
        if (!current.Available || current.Detail is not null)
        {
            return new HidHideAllowResult(null, current.Detail ?? "HidHide is not installed.");
        }

        if (current.Inverse)
        {
            return new HidHideAllowResult(null,
                "HidHide is in inverse mode, where the list denies instead of allows; adding this tool would hide devices from it.");
        }

        if (Contains(current.Applications, selfPath, DeviceForDrive))
        {
            return new HidHideAllowResult(null, "This tool is already allowed.");
        }

        state.Update(changes => changes with { HidHideEntry = selfPath, HidHideAddedAt = DateTimeOffset.UtcNow });
        var error = device.WriteApplications([.. current.Applications, selfPath]);
        if (error is null)
        {
            return new HidHideAllowResult(selfPath, null);
        }

        state.Update(changes => changes with { HidHideEntry = null, HidHideAddedAt = null });
        return new HidHideAllowResult(null, error);
    }

    /// <summary>Whether HidHide is in inverse mode and lists this tool, which blocks it from the devices.</summary>
    /// <param name="current">State from <see cref="Read" />.</param>
    /// <returns>True when this tool is denied.</returns>
    public bool DeniedByInverseList(HidHideState current)
    {
        return current is { Available: true, Inverse: true } &&
               Contains(current.Applications, selfPath, DeviceForDrive);
    }

    /// <summary>Whether this tool is allowed already.</summary>
    /// <param name="current">State from <see cref="Read" />.</param>
    /// <returns>True when it is listed in a normal list.</returns>
    public bool AlreadyAllowed(HidHideState current)
    {
        return current is { Available: true, Inverse: false } &&
               Contains(current.Applications, selfPath, DeviceForDrive);
    }

    /// <summary>
    ///     Keeps an entry an earlier session left behind, at the tester's choice: the record is cleared so
    ///     it is no longer removed automatically, and the entry stays in HidHide.
    /// </summary>
    /// <returns>The entry that was kept, or null.</returns>
    public string? KeepRecorded()
    {
        var added = state.Read().HidHideEntry;
        state.Update(changes => changes with { HidHideEntry = null, HidHideAddedAt = null });
        return added;
    }

    /// <summary>Removes the entry recorded in the machine state, leaving every other entry as it is now.</summary>
    /// <returns>Null when nothing is recorded or the list reads back without it; otherwise the problem.</returns>
    public string? RestoreRecorded()
    {
        var added = state.Read().HidHideEntry;
        if (added is null)
        {
            return null;
        }

        var current = device.Read();
        if (!current.Available || current.Detail is not null)
        {
            return current.Detail ?? "HidHide is not available, so the entry could not be removed.";
        }

        List<string> remaining = [.. current.Applications.Where(entry => !IsEntry(entry, added))];
        if (remaining.Count != current.Applications.Count && device.WriteApplications(remaining) is { } error)
        {
            return error;
        }

        var after = device.Read();
        if (!after.Available || after.Detail is not null || after.Applications.Any(entry => IsEntry(entry, added)))
        {
            return "The entry was still listed after removing it.";
        }

        state.Update(changes => changes with { HidHideEntry = null, HidHideAddedAt = null });
        return null;
    }

    /// <summary>Whether a list already holds an application, in either path notation.</summary>
    /// <param name="entries">Entries as HidHide returned them.</param>
    /// <param name="value">Application path.</param>
    /// <param name="deviceForDrive">Maps a drive such as <c>C:</c> to its NT device, or null.</param>
    /// <returns>Whether it is present.</returns>
    public static bool Contains(IEnumerable<string> entries, string value, Func<string, string?> deviceForDrive)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = NormalizePath(value, deviceForDrive);
        return entries.Any(entry => string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)
                                    || (normalized.Length > 0
                                        && string.Equals(NormalizePath(entry, deviceForDrive), normalized,
                                            StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Writes a path in NT device notation, which HidHide uses, keeping the volume.</summary>
    /// <param name="value">A drive-letter path or an NT device path.</param>
    /// <param name="deviceForDrive">Maps a drive such as <c>C:</c> to its NT device, or null.</param>
    /// <returns>
    ///     The comparable form. A drive that does not map stays in drive notation, so it never matches a
    ///     device path and the tester is asked instead of skipped.
    /// </returns>
    public static string NormalizePath(string value, Func<string, string?> deviceForDrive)
    {
        ArgumentNullException.ThrowIfNull(deviceForDrive);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var path = value.Trim().Replace('/', '\\');
        if (path.Length > 2 && path[1] == ':' && path[2] == '\\' && char.IsAsciiLetter(path[0])
            && deviceForDrive(path[..2]) is { Length: > 0 } device)
        {
            return device.TrimEnd('\\') + path[2..];
        }

        return path;
    }

    // Only the exact string the lab wrote is its own; a matching entry in the other notation was put
    // there by someone else.
    private static bool IsEntry(string entry, string added)
    {
        return string.Equals(entry, added, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DeviceForDrive(string drive)
    {
        var buffer = new char[1024];
        var length = QueryDosDevice(drive, buffer, (uint)buffer.Length);
        if (length == 0)
        {
            return null;
        }

        // SUBST and network drives map to \??\ or redirector targets; only a plain volume is compared.
        var target = new string(buffer, 0, (int)length).Split('\0')[0];
        return target.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string device, [Out] char[] target, uint targetLength);

    private sealed class NativeHidHideDevice : IHidHideDevice
    {
        public HidHideState Read()
        {
            if (!NativeHidHide.TryOpen(out var handle, out var openError))
            {
                return new HidHideState(false, false, false, [],
                    $"HidHide is not installed or not running (error {openError}).");
            }

            using (handle)
            {
                if (!NativeHidHide.TryReadBoolean(handle, NativeHidHide.GetActive, out var active, out var error)
                    || !NativeHidHide.TryReadBoolean(handle, NativeHidHide.GetInverse, out var inverse, out error)
                    || !NativeHidHide.TryReadMultiString(handle, NativeHidHide.GetApplications, out var applications,
                        out error))
                {
                    return new HidHideState(true, false, false, [], $"HidHide could not be read (error {error}).");
                }

                return new HidHideState(true, active, inverse, applications, null);
            }
        }

        public string? WriteApplications(IReadOnlyList<string> applications)
        {
            if (applications.Any(entry => string.IsNullOrWhiteSpace(entry) || entry.Contains('\0')))
            {
                return "A HidHide entry is empty or contains a NUL.";
            }

            if (!NativeHidHide.TryOpen(out var handle, out var openError))
            {
                return $"HidHide could not be opened for writing (error {openError}).";
            }

            using (handle)
            {
                return NativeHidHide.TryWriteMultiString(handle, NativeHidHide.SetApplications, applications,
                    out var error)
                    ? null
                    : $"HidHide refused the list; nothing was changed (error {error}).";
            }
        }
    }
}
