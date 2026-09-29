using System.Collections.Generic;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>What HidHide's control device reported.</summary>
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

internal interface IHidHideControl
{
    HidHideControlState Read();

    int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries);

    int WriteActive(bool active);
}

internal sealed class NativeHidHideControl : IHidHideControl
{
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
