// Shared between WSGM and Device Lab (linked as a source file): the one open-read-write sequence on
// HidHide's control device. It depends only on WSGM.Interop and logs nothing.

using System.Collections.Generic;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Which HidHide list an entry belongs to.</summary>
internal enum HidHideEntryKind
{
    /// <summary>Executable paths permitted by the driver's application list.</summary>
    Application,

    /// <summary>Device instance paths controlled by the driver's device list.</summary>
    Device
}

/// <summary>What HidHide's control device reported.</summary>
/// <param name="Succeeded">Whether all state reads succeeded; failed snapshots carry empty lists.</param>
/// <param name="Error">Win32 error from the failed operation, or zero.</param>
/// <param name="Active">Whether hiding is enabled.</param>
/// <param name="Inverse">Whether the application list uses inverted matching.</param>
/// <param name="Applications">Current executable-path list.</param>
/// <param name="Devices">Current hidden-device instance list.</param>
internal sealed record HidHideControlState(
    bool Succeeded,
    int Error,
    bool Active,
    bool Inverse,
    IReadOnlyList<string> Applications,
    IReadOnlyList<string> Devices)
{
    /// <summary>Whether a failed open means the driver is not installed at all.</summary>
    /// <param name="error">The Win32 error the open returned.</param>
    /// <returns>True for file or path not found.</returns>
    internal static bool IsNotInstalled(int error)
    {
        return error is 2 or 3;
    }
}

/// <summary>Reads or replaces driver state; ownership journaling and recovery remain with the caller.</summary>
internal interface IHidHideControl
{
    /// <summary>Reads the active flag, inversion flag and both lists through one opened control handle.</summary>
    /// <returns>The complete state, or a failed snapshot with the native error.</returns>
    HidHideControlState Read();

    /// <summary>Replaces one complete driver list without a confirming read.</summary>
    /// <param name="entryKind">The application or device list to replace.</param>
    /// <param name="entries">Complete desired list, including entries owned by other software.</param>
    /// <returns>Zero when accepted, otherwise the Win32 error.</returns>
    int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries);

    /// <summary>Sets the driver's global active flag without changing either list.</summary>
    /// <param name="active">Desired hiding state.</param>
    /// <returns>Zero when accepted, otherwise the Win32 error.</returns>
    int WriteActive(bool active);
}

/// <summary>Opens and closes HidHide's control device independently for each operation.</summary>
internal sealed class NativeHidHideControl : IHidHideControl
{
    /// <inheritdoc />
    public HidHideControlState Read()
    {
        if (!NativeHidHide.TryOpen(out var handle, out var error))
        {
            return Failure(error);
        }

        using (handle)
        {
            if (!NativeHidHide.TryReadBoolean(
                    handle,
                    NativeHidHide.GetActive,
                    out var active,
                    out error)
                || !NativeHidHide.TryReadBoolean(
                    handle,
                    NativeHidHide.GetInverse,
                    out var inverse,
                    out error)
                || !NativeHidHide.TryReadMultiString(
                    handle,
                    NativeHidHide.GetApplications,
                    out var applications,
                    out error)
                || !NativeHidHide.TryReadMultiString(
                    handle,
                    NativeHidHide.GetDevices,
                    out var devices,
                    out error))
            {
                return Failure(error);
            }

            return new HidHideControlState(true, 0, active, inverse, applications, devices);
        }
    }

    /// <inheritdoc />
    public int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries)
    {
        if (!NativeHidHide.TryOpen(out var handle, out var error))
        {
            return error;
        }

        using (handle)
        {
            var code = entryKind is HidHideEntryKind.Application
                ? NativeHidHide.SetApplications
                : NativeHidHide.SetDevices;
            return NativeHidHide.TryWriteMultiString(handle, code, entries, out error)
                ? 0
                : error;
        }
    }

    /// <inheritdoc />
    public int WriteActive(bool active)
    {
        if (!NativeHidHide.TryOpen(out var handle, out var error))
        {
            return error;
        }

        using (handle)
        {
            return NativeHidHide.TryWriteBoolean(handle, NativeHidHide.SetActive, active, out error)
                ? 0
                : error;
        }
    }

    private static HidHideControlState Failure(int error)
    {
        return new HidHideControlState(false, error, false, false, [], []);
    }
}
