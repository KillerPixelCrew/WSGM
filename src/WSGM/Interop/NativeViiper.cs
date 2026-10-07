using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>
///     The flat C ABI of <c>libviiper</c>, WSGM's virtual-USB controller backend.
/// </summary>
/// <remarks>
///     VIIPER runs its USBIP server in-process behind this ABI, so a virtual controller needs no helper
///     process. Every signature here is blittable, keeping the native ownership boundary small and
///     explicit.
///     <para>
///         The kernel side is <c>usbip-win2</c>'s generic signed driver, installed once by the installer.
///         Nothing in this file installs, repairs, or elevates anything; a missing library or driver simply
///         makes controller management unavailable.
///     </para>
/// </remarks>
internal static partial class NativeViiper
{
    private const string Library = "libviiper";

    /// <summary>Return value of every entry point that succeeded.</summary>
    internal const int Ok = 0;

    /// <summary>Starts the process-local VIIPER server; caller serializes initialization.</summary>
    /// <param name="listenAddress">UTF-8 host:port listener address.</param>
    /// <returns>Zero on success; otherwise read TakeLastError for diagnostic detail.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_init", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Init(string listenAddress);

    /// <summary>Stops the server and releases every bus and device it owns.</summary>
    [LibraryImport(Library, EntryPoint = "viiper_shutdown")]
    internal static partial void Shutdown();

    /// <summary>Creates a logical bus under the initialized VIIPER server.</summary>
    /// <param name="busId">Caller-selected bus identity.</param>
    /// <returns>Zero on success; otherwise read TakeLastError for diagnostic detail.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_bus_create")]
    internal static partial int BusCreate(uint busId);

    /// <summary>Adds a virtual protocol device before host attachment.</summary>
    /// <param name="busId">Existing bus identity.</param>
    /// <param name="typeName">Supported UTF-8 VIIPER device type name.</param>
    /// <param name="deviceId">New device identity on success; caller owns removal.</param>
    /// <returns>Zero on success; otherwise read TakeLastError for diagnostic detail.</returns>
    [LibraryImport(
        Library,
        EntryPoint = "viiper_device_add",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int DeviceAdd(uint busId, string typeName, out uint deviceId);

    /// <summary>Requests USB/IP host attachment for an existing device.</summary>
    /// <param name="busId">Existing bus identity.</param>
    /// <param name="deviceId">Device to attach after its neutral first report is installed.</param>
    /// <returns>Zero on success; otherwise read TakeLastError for diagnostic detail.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_attach")]
    internal static partial int DeviceAttach(uint busId, uint deviceId);

    /// <summary>Removes the device and invalidates its fast-submission handle.</summary>
    /// <param name="busId">Existing bus identity.</param>
    /// <param name="deviceId">Device whose host and server resources must be released.</param>
    /// <returns>Zero on success; otherwise read TakeLastError for diagnostic detail.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_remove")]
    internal static partial int DeviceRemove(uint busId, uint deviceId);

    /// <summary>Opens the lock-free submission handle for a device.</summary>
    /// <remarks>
    ///     The fast path exists because the ordinary submission entry point takes the library's global
    ///     mutex, which is the wrong cost on a path that runs at the controller's poll rate.
    /// </remarks>
    /// <param name="busId">Existing VIIPER bus identity.</param>
    /// <param name="deviceId">Existing device identity on the bus.</param>
    /// <param name="handle">Fast submission handle valid only while this device exists.</param>
    /// <returns>Zero on success; otherwise read TakeLastError.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_open_fast")]
    internal static partial int DeviceOpenFast(uint busId, uint deviceId, out uint handle);

    /// <summary>Submits one input frame through the fast path.</summary>
    /// <remarks>The buffer is decoded synchronously and never retained by the library.</remarks>
    /// <param name="handle">Live fast handle from DeviceOpenFast.</param>
    /// <param name="data">Borrowed report bytes consumed synchronously; pin for the duration of this call.</param>
    /// <param name="length">Report byte count for the selected virtual protocol.</param>
    /// <returns>Zero when accepted; otherwise read TakeLastError and stop using a lost target.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_set_input_fast")]
    internal static unsafe partial int DeviceSetInputFast(uint handle, byte* data, int length);

    /// <summary>Registers the host-to-device feedback callback; it runs on a library thread.</summary>
    /// <param name="busId">Existing VIIPER bus identity.</param>
    /// <param name="deviceId">Device whose host output is observed.</param>
    /// <param name="callback">Cdecl callback invoked on a library thread; must not throw or retain the borrowed report buffer.</param>
    /// <param name="userData">Caller-owned context kept alive through callback quiescence; WSGM retains it until Shutdown completes.</param>
    /// <returns>Zero when registered; otherwise read TakeLastError.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_set_feedback_callback")]
    internal static unsafe partial int DeviceSetFeedbackCallback(
        uint busId,
        uint deviceId,
        delegate* unmanaged[Cdecl]<uint, uint, byte*, int, void*, void> callback,
        void* userData);

    /// <summary>Returns the last error text, or null; release it with <see cref="FreeString" />.</summary>
    [LibraryImport(Library, EntryPoint = "viiper_last_error")]
    private static partial IntPtr LastError();

    [LibraryImport(Library, EntryPoint = "viiper_free_string")]
    private static partial void FreeString(IntPtr value);

    /// <summary>Reads and releases the library's last error message.</summary>
    /// <returns>The message, or a stable placeholder when the library reported none.</returns>
    internal static string TakeLastError()
    {
        var text = IntPtr.Zero;
        try
        {
            text = LastError();
            return text == IntPtr.Zero
                ? "The controller backend reported no detail."
                : Marshal.PtrToStringUTF8(text) ?? "The controller backend reported no detail.";
        }
        catch (DllNotFoundException)
        {
            return "The controller backend library is not installed.";
        }
        catch (EntryPointNotFoundException)
        {
            return "The installed controller backend library is the wrong version.";
        }
        finally
        {
            if (text != IntPtr.Zero)
            {
                FreeString(text);
            }
        }
    }
}
