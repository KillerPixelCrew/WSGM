using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Interop;

/// <summary>Flat Win32 storage interop for the taskbar's Safe Eject feature:
/// volume-to-disk mapping (IOCTL_STORAGE_GET_DEVICE_NUMBER), the hotplug
/// classification that separates a USB device from a built-in card reader
/// (IOCTL_STORAGE_GET_HOTPLUG_INFO), the PnP device eject
/// (CM_Request_Device_EjectW) and the media-level dismount sequence
/// (FSCTL_LOCK_VOLUME → FSCTL_DISMOUNT_VOLUME → IOCTL_STORAGE_EJECT_MEDIA).
///
/// Everything here is cfgmgr32/kernel32 — no COM, no WMI — so it is legal under
/// the NativeAOT publish. Devnode discovery goes through the cfgmgr32 interface
/// list rather than SetupAPI's devinfo sets: same data, no variable-size
/// detail-struct marshalling.
///
/// The two fixed-layout records are decoded from documented offsets by pure
/// span readers, so the layouts are unit-testable from a synthetic buffer (the
/// NativeRadio approach).</summary>
internal static unsafe partial class NativeStorage
{
    // ---- return codes / constants ----

    /// <summary>CONFIGRET success.</summary>
    internal const int CrSuccess = 0;

    /// <summary>CONFIGRET: the eject was vetoed; the veto type and name say why.</summary>
    internal const int CrRemoveVetoed = 0x17;

    /// <summary>CM_DEVCAP_REMOVABLE: the devnode itself can be ejected.</summary>
    internal const uint DevCapRemovable = 0x4;

    // CM_DRP_* registry properties (SPDRP value + 1).
    private const uint DrpDeviceDesc = 0x01;
    private const uint DrpFriendlyName = 0x0D;
    private const uint DrpCapabilities = 0x10;

    /// <summary>STORAGE_DEVICE_NUMBER.DeviceType for a disk.</summary>
    internal const int FileDeviceDisk = 0x7;

    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const uint IoctlStorageGetHotplugInfo = 0x2D0C14;
    private const uint FsctlLockVolume = 0x090018;
    private const uint FsctlDismountVolume = 0x090020;
    private const uint IoctlStorageMediaRemoval = 0x2D4804;
    private const uint IoctlStorageEjectMedia = 0x2D4808;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    /// <summary>GUID_DEVINTERFACE_DISK: every present disk exposes one of these
    /// interfaces; enumerating them is how a volume's device number becomes a
    /// devnode.</summary>
    private static Guid DiskInterfaceGuid { get; } =
        new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>How Windows says an eject was refused (cfg.h PNP_VETO_TYPE,
    /// zero-based).</summary>
    internal enum PnpVetoType
    {
        /// <summary>No reason was named.</summary>
        TypeUnknown = 0,

        /// <summary>A legacy device cannot be ejected.</summary>
        LegacyDevice = 1,

        /// <summary>A close is still pending on the device.</summary>
        PendingClose = 2,

        /// <summary>An application vetoed; the veto name is a module.</summary>
        WindowsApp = 3,

        /// <summary>A service vetoed; the veto name is a service name.</summary>
        WindowsService = 4,

        /// <summary>Open handles remain on the device.</summary>
        OutstandingOpen = 5,

        /// <summary>The device itself refused.</summary>
        Device = 6,

        /// <summary>The driver refused.</summary>
        Driver = 7,

        /// <summary>The request is illegal for this device.</summary>
        IllegalDeviceRequest = 8,

        /// <summary>Insufficient power to complete the operation.</summary>
        InsufficientPower = 9,

        /// <summary>The device cannot be disabled.</summary>
        NonDisableable = 10,

        /// <summary>A legacy driver vetoed.</summary>
        LegacyDriver = 11,

        /// <summary>The caller lacks the rights to eject.</summary>
        InsufficientRights = 12,
    }

    // ---- kernel32 ----

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    // ---- cfgmgr32 ----

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List_SizeW(
        out uint length, in Guid interfaceClassGuid, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_ListW(
        in Guid interfaceClassGuid, string? deviceId, char* buffer, uint bufferLength,
        uint flags);

    /// <summary>DEVPROPKEY, blittable: a property category GUID plus an id.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    /// <summary>DEVPKEY_Device_InstanceId.</summary>
    private static readonly DevPropKey DevicePropertyInstanceId = new()
    {
        Fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
        Pid = 256,
    };

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_PropertyW(
        string deviceInterface, in DevPropKey propertyKey, out uint propertyType,
        char* propertyBuffer, ref uint propertyBufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(
        out uint devInst, string deviceInstanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial int CM_Get_DevNode_Registry_PropertyW(
        uint devInst, uint property, out uint regDataType, byte* buffer, ref uint length,
        uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW")]
    private static partial int CM_Request_Device_EjectW(
        uint devInst, out int vetoType, char* vetoName, uint nameLength, uint flags);

    // ---- fixed-layout record readers (unit-tested from synthetic buffers) ----

    /// <summary>Size of a STORAGE_DEVICE_NUMBER record.</summary>
    internal const int DeviceNumberRecordSize = 12;

    /// <summary>Size of a STORAGE_HOTPLUG_INFO record.</summary>
    internal const int HotplugRecordSize = 8;

    /// <summary>Decodes a STORAGE_DEVICE_NUMBER buffer.</summary>
    /// <param name="buffer">At least <see cref="DeviceNumberRecordSize"/> bytes.</param>
    internal static (int DeviceType, int DeviceNumber, int PartitionNumber) ReadDeviceNumber(
        ReadOnlySpan<byte> buffer) =>
        (BitConverter.ToInt32(buffer),
            BitConverter.ToInt32(buffer[4..]),
            BitConverter.ToInt32(buffer[8..]));

    /// <summary>Decodes a STORAGE_HOTPLUG_INFO buffer: whether the media is
    /// removable from the device, and whether the device itself is hot-pluggable.</summary>
    /// <param name="buffer">At least <see cref="HotplugRecordSize"/> bytes.</param>
    internal static (bool MediaRemovable, bool DeviceHotplug) ReadHotplugInfo(
        ReadOnlySpan<byte> buffer) => (buffer[4] != 0, buffer[6] != 0);

    // ---- queries ----

    /// <summary>Opens a volume for attribute queries only — zero access needs no
    /// privilege and touches no media.</summary>
    /// <param name="letter">The drive letter.</param>
    internal static SafeFileHandle OpenVolumeForQuery(char letter) =>
        CreateFileW($@"\\.\{letter}:", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Opens a volume for the lock/dismount/eject sequence.</summary>
    /// <param name="letter">The drive letter.</param>
    internal static SafeFileHandle OpenVolumeForEject(char letter) =>
        CreateFileW($@"\\.\{letter}:", GenericRead | GenericWrite, FileShareReadWrite, 0,
            OpenExisting, 0, 0);

    /// <summary>Opens a device-interface path for attribute queries only.</summary>
    /// <param name="path">A path from <see cref="ListDiskInterfaces"/>.</param>
    internal static SafeFileHandle OpenVolumeForQueryPath(string path) =>
        CreateFileW(path, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Opens a physical disk for attribute queries only.</summary>
    /// <param name="number">The disk number.</param>
    internal static SafeFileHandle OpenDiskForQuery(int number) =>
        CreateFileW($@"\\.\PhysicalDrive{number}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Reads which physical disk (and partition) a volume lives on.</summary>
    /// <param name="volume">An open volume handle.</param>
    /// <param name="deviceType">The FILE_DEVICE_* type of the underlying device.</param>
    /// <param name="deviceNumber">The physical disk number.</param>
    internal static bool TryGetDeviceNumber(
        SafeFileHandle volume, out int deviceType, out int deviceNumber)
    {
        var buffer = stackalloc byte[DeviceNumberRecordSize];
        if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, 0, 0, (nint)buffer,
                DeviceNumberRecordSize, out var written, 0)
            || written < DeviceNumberRecordSize)
        {
            deviceType = 0;
            deviceNumber = -1;
            return false;
        }
        (deviceType, deviceNumber, _) =
            ReadDeviceNumber(new ReadOnlySpan<byte>(buffer, DeviceNumberRecordSize));
        return true;
    }

    /// <summary>Reads the disk's hotplug facts — the classification that decides
    /// between a device-level and a media-level eject.</summary>
    /// <param name="disk">An open physical-disk handle.</param>
    /// <param name="mediaRemovable">Whether the media can leave the device.</param>
    /// <param name="deviceHotplug">Whether the device itself is hot-pluggable.</param>
    internal static bool TryGetHotplugInfo(
        SafeFileHandle disk, out bool mediaRemovable, out bool deviceHotplug)
    {
        var buffer = stackalloc byte[HotplugRecordSize];
        if (!DeviceIoControl(disk, IoctlStorageGetHotplugInfo, 0, 0, (nint)buffer,
                HotplugRecordSize, out var written, 0)
            || written < HotplugRecordSize)
        {
            mediaRemovable = false;
            deviceHotplug = false;
            return false;
        }
        (mediaRemovable, deviceHotplug) =
            ReadHotplugInfo(new ReadOnlySpan<byte>(buffer, HotplugRecordSize));
        return true;
    }

    /// <summary>Lists the device-interface paths of every present disk.</summary>
    internal static string[] ListDiskInterfaces()
    {
        var guid = DiskInterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out var length, in guid, null, 0) != CrSuccess
            || length < 2)
        {
            return [];
        }
        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            if (CM_Get_Device_Interface_ListW(in guid, null, p, length, 0) != CrSuccess)
            {
                return [];
            }
        }
        // Double-NUL-terminated multi-string.
        var result = new System.Collections.Generic.List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }
            if (i > start)
            {
                result.Add(new string(buffer, start, i - start));
            }
            start = i + 1;
        }
        return [.. result];
    }

    /// <summary>Resolves a device-interface path to its devnode.</summary>
    /// <param name="interfacePath">A path from <see cref="ListDiskInterfaces"/>.</param>
    /// <param name="devInst">The devnode handle.</param>
    internal static bool TryGetDevNode(string interfacePath, out uint devInst)
    {
        devInst = 0;
        var size = 1024u;
        var buffer = stackalloc char[512];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, in DevicePropertyInstanceId,
                out _, buffer, ref size, 0) != CrSuccess)
        {
            return false;
        }
        var instanceId = new string(buffer);
        return instanceId.Length > 0
            && CM_Locate_DevNodeW(out devInst, instanceId, 0) == CrSuccess;
    }

    /// <summary>Reads the devnode's device instance path — the stable identity a
    /// list row keys on.</summary>
    /// <param name="devInst">The devnode.</param>
    internal static string GetDeviceInstanceId(uint devInst)
    {
        // CM_Get_Device_IDW; capped at MAX_DEVICE_ID_LEN (200).
        var buffer = stackalloc char[200];
        return CM_Get_Device_IDW(devInst, buffer, 200, 0) == CrSuccess
            ? new string(buffer)
            : "";
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial int CM_Get_Device_IDW(
        uint devInst, char* buffer, uint bufferLength, uint flags);

    /// <summary>Reads the devnode's display name: the friendly name when set,
    /// else the device description, else an empty string.</summary>
    /// <param name="devInst">The devnode.</param>
    internal static string GetDeviceDisplayName(uint devInst)
    {
        var name = ReadDevNodeString(devInst, DrpFriendlyName);
        return name.Length > 0 ? name : ReadDevNodeString(devInst, DrpDeviceDesc);
    }

    private static string ReadDevNodeString(uint devInst, uint property)
    {
        var length = 1024u;
        var buffer = stackalloc byte[1024];
        if (CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0)
            != CrSuccess)
        {
            return "";
        }
        return Marshal.PtrToStringUni((nint)buffer) ?? "";
    }

    /// <summary>Reads the devnode's CM_DEVCAP_* capability bits, 0 on failure.</summary>
    /// <param name="devInst">The devnode.</param>
    internal static uint GetCapabilities(uint devInst)
    {
        var length = 4u;
        uint capabilities = 0;
        return CM_Get_DevNode_Registry_PropertyW(devInst, DrpCapabilities, out _,
                (byte*)&capabilities, ref length, 0) == CrSuccess
            ? capabilities
            : 0;
    }

    /// <summary>Walks from a disk devnode to the node the PnP eject should
    /// target: the first ancestor (or the disk itself) whose capabilities say
    /// CM_DEVCAP_REMOVABLE. For USB storage that is the USB device above the
    /// USBSTOR disk — ejecting the disk node itself commonly fails. Falls back
    /// to the immediate parent when no ancestor claims removability.</summary>
    /// <param name="diskDevInst">The disk devnode.</param>
    internal static uint FindEjectTarget(uint diskDevInst)
    {
        var node = diskDevInst;
        for (var depth = 0; depth < 4; depth++)
        {
            if ((GetCapabilities(node) & DevCapRemovable) != 0)
            {
                return node;
            }
            if (CM_Get_Parent(out var parent, node, 0) != CrSuccess)
            {
                break;
            }
            node = parent;
        }
        // Nothing claimed removability: the classic fallback is the disk's parent.
        return CM_Get_Parent(out var fallback, diskDevInst, 0) == CrSuccess
            ? fallback
            : diskDevInst;
    }

    // ---- eject operations ----

    /// <summary>Asks PnP to eject a device — the same operation as Explorer's
    /// "Safely Remove Hardware". Dismounts and flushes every volume on the
    /// device.</summary>
    /// <param name="devInst">The devnode to eject (see <see cref="FindEjectTarget"/>).</param>
    /// <param name="vetoType">Why the eject was refused, when it was.</param>
    /// <param name="vetoName">The vetoing module/service/path, possibly empty.</param>
    /// <returns>The CONFIGRET code: <see cref="CrSuccess"/>, <see cref="CrRemoveVetoed"/>,
    /// or another CR_* failure.</returns>
    internal static int RequestDeviceEject(
        uint devInst, out PnpVetoType vetoType, out string vetoName)
    {
        const int MaxPath = 260;
        var buffer = stackalloc char[MaxPath];
        var result = CM_Request_Device_EjectW(devInst, out var rawVeto, buffer, MaxPath, 0);
        vetoType = (PnpVetoType)rawVeto;
        vetoName = Marshal.PtrToStringUni((nint)buffer) ?? "";
        return result;
    }

    /// <summary>Takes the exclusive volume lock — the open-files check for the
    /// media-level eject. Fails while any other handle is open on the volume.</summary>
    /// <param name="volume">A volume opened via <see cref="OpenVolumeForEject"/>.</param>
    internal static bool LockVolume(SafeFileHandle volume) =>
        DeviceIoControl(volume, FsctlLockVolume, 0, 0, 0, 0, out _, 0);

    /// <summary>Dismounts the file system, flushing it first.</summary>
    /// <param name="volume">A locked volume handle.</param>
    internal static bool DismountVolume(SafeFileHandle volume) =>
        DeviceIoControl(volume, FsctlDismountVolume, 0, 0, 0, 0, out _, 0);

    /// <summary>Releases any software media lock (PREVENT_MEDIA_REMOVAL = FALSE),
    /// then asks the device to eject the media. Card readers without a motor may
    /// fail the final call — the caller treats lock+dismount as the real
    /// success.</summary>
    /// <param name="volume">A locked, dismounted volume handle.</param>
    internal static bool EjectMedia(SafeFileHandle volume)
    {
        byte allow = 0;
        DeviceIoControl(volume, IoctlStorageMediaRemoval, (nint)(&allow), 1, 0, 0, out _, 0);
        return DeviceIoControl(volume, IoctlStorageEjectMedia, 0, 0, 0, 0, out _, 0);
    }

    /// <summary>The calling thread's last Win32 error, for log lines.</summary>
    internal static int LastWin32Error() => Marshal.GetLastPInvokeError();
}
