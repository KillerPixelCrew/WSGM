using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>
/// The flat C ABI of <c>libviiper</c>, WSGM's virtual-USB controller backend.
/// </summary>
/// <remarks>
/// VIIPER runs its USBIP server in-process behind this ABI, so a virtual controller needs no helper
/// process. Every signature here is blittable, keeping the native ownership boundary small and
/// explicit.
/// <para>
/// The kernel side is <c>usbip-win2</c>'s generic signed driver, installed once by the installer.
/// Nothing in this file installs, repairs, or elevates anything; a missing library or driver simply
/// makes controller management unavailable.
/// </para>
/// </remarks>
internal static partial class NativeViiper
{
    private const string Library = "libviiper";

    /// <summary>Return value of every entry point that succeeded.</summary>
    internal const int Ok = 0;

    /// <summary>Starts the in-process USBIP server.</summary>
    /// <param name="listenAddress">Loopback address and port to bind.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_init", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Init(string listenAddress);

    /// <summary>Stops the server and releases every device it owns.</summary>
    [LibraryImport(Library, EntryPoint = "viiper_shutdown")]
    internal static partial void Shutdown();

    /// <summary>Creates a virtual bus.</summary>
    /// <param name="busId">Identifier for the new bus.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_bus_create")]
    internal static partial int BusCreate(uint busId);

    /// <summary>Removes a virtual bus.</summary>
    /// <param name="busId">The bus to remove.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_bus_remove")]
    internal static partial int BusRemove(uint busId);

    /// <summary>Adds a device of a named type to a bus.</summary>
    /// <param name="busId">The owning bus.</param>
    /// <param name="typeName">Device type, for example <c>steamdeck</c>.</param>
    /// <param name="deviceId">Receives the new device identifier.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    [LibraryImport(
        Library,
        EntryPoint = "viiper_device_add",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int DeviceAdd(uint busId, string typeName, out uint deviceId);

    /// <summary>Attaches a device so the host enumerates it.</summary>
    /// <param name="busId">The owning bus.</param>
    /// <param name="deviceId">The device to attach.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_attach")]
    internal static partial int DeviceAttach(uint busId, uint deviceId);

    /// <summary>Removes a device, detaching it from the host.</summary>
    /// <param name="busId">The owning bus.</param>
    /// <param name="deviceId">The device to remove.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_remove")]
    internal static partial int DeviceRemove(uint busId, uint deviceId);

    /// <summary>Opens the lock-free submission handle for a device.</summary>
    /// <param name="busId">The owning bus.</param>
    /// <param name="deviceId">The device to open.</param>
    /// <param name="handle">Receives the fast-path handle.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    /// <remarks>
    /// The fast path exists because the ordinary submission entry point takes the library's global
    /// mutex, which is the wrong cost on a path that runs at the controller's poll rate.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "viiper_device_open_fast")]
    internal static partial int DeviceOpenFast(uint busId, uint deviceId, out uint handle);

    /// <summary>Submits one input frame through the fast path.</summary>
    /// <param name="handle">Handle from <see cref="DeviceOpenFast"/>.</param>
    /// <param name="data">The device's wire-format frame.</param>
    /// <param name="length">Length of <paramref name="data"/>.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    /// <remarks>The buffer is decoded synchronously and never retained by the library.</remarks>
    [LibraryImport(Library, EntryPoint = "viiper_device_set_input_fast")]
    internal static unsafe partial int DeviceSetInputFast(uint handle, byte* data, int length);

    /// <summary>Registers the host-to-device feedback callback, such as rumble.</summary>
    /// <param name="busId">The owning bus.</param>
    /// <param name="deviceId">The device to observe.</param>
    /// <param name="callback">Callback invoked on a library thread.</param>
    /// <param name="userData">Opaque pointer passed back to the callback.</param>
    /// <returns><see cref="Ok"/> on success.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_device_set_feedback_callback")]
    internal static unsafe partial int DeviceSetFeedbackCallback(
        uint busId,
        uint deviceId,
        delegate* unmanaged[Cdecl]<uint, uint, byte*, int, void*, void> callback,
        void* userData);

    /// <summary>Returns the last error text, or null.</summary>
    /// <returns>An unmanaged string the caller must release with <see cref="FreeString"/>.</returns>
    [LibraryImport(Library, EntryPoint = "viiper_last_error")]
    internal static partial IntPtr LastError();

    /// <summary>Releases a string returned by the library.</summary>
    /// <param name="value">The string to release.</param>
    [LibraryImport(Library, EntryPoint = "viiper_free_string")]
    internal static partial void FreeString(IntPtr value);

    /// <summary>Reads and releases the library's last error message.</summary>
    /// <returns>The message, or a stable placeholder when the library reported none.</returns>
    internal static string TakeLastError()
    {
        IntPtr text = IntPtr.Zero;
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
