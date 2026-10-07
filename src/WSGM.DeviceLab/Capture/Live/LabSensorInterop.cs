using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.DeviceLab.Capture.Live;

// The legacy Sensor API COM ABI (sensorsapi.h, portabledeviceapi.h), the serial port interface list
// and a high-resolution wait. The COM declarations match the SDK's LegacyMotionSensors, which the Claw
// plugin's WindowsMotionSource uses. HID goes through the SDK's HidDevices.
internal static partial class LabSensorInterop
{
    /// <summary>VT_LPWSTR.</summary>
    public const ushort VtLpwstr = 31;

    /// <summary>VT_UI4.</summary>
    public const ushort VtUi4 = 19;

    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;

    /// <summary>SENSOR_CATEGORY_ALL.</summary>
    public static readonly Guid CategoryAll = new("C317C286-C468-4288-9975-D4C4587C442C");

    /// <summary>SENSOR_CATEGORY_MOTION.</summary>
    public static readonly Guid CategoryMotion = new("CD09DAF1-3B2E-4C3D-B598-B5E5FF93FD46");

    /// <summary>SENSOR_CATEGORY_ORIENTATION.</summary>
    public static readonly Guid CategoryOrientation = new("9E6C04B6-96FE-4954-B726-68682A473F69");

    /// <summary>SENSOR_TYPE_ACCELEROMETER_3D.</summary>
    public static readonly Guid TypeAccelerometer3D = new("C2FB0F5F-E2D2-4C78-BCD0-352A9582819D");

    /// <summary>SENSOR_TYPE_GYROMETER_3D.</summary>
    public static readonly Guid TypeGyrometer3D = new("09485F5A-759E-42C2-BD4B-A349B75C8643");

    /// <summary>SENSOR_TYPE_CUSTOM.</summary>
    public static readonly Guid TypeCustom = new("E83AF229-8640-4D18-A213-E22675EBB2C3");

    /// <summary>SENSOR_DATA_TYPE_MOTION_GUID: acceleration (pid 2-4, g) and angular velocity (pid 10-12, deg/s).</summary>
    public static readonly Guid MotionData = new("3F8A69A2-07C5-4E48-A965-CD797AAB56D5");

    /// <summary>SENSOR_DATA_TYPE_CUSTOM_GUID: custom value fields.</summary>
    public static readonly Guid CustomData = new("B14C764F-07CF-41E8-9D82-EBE3D0776A6F");

    /// <summary>SENSOR_PROPERTY_COMMON_GUID: report intervals (pid 12 minimum, 13 current) and device path (pid 15).</summary>
    public static readonly Guid CommonProperties = new("7F8383EC-D3EC-495C-A8CF-B8BBE85C2920");

    /// <summary>GUID_DEVINTERFACE_COMPORT.</summary>
    public static readonly Guid ComPortInterface = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");

    /// <summary>Lists the present interfaces of one device interface class.</summary>
    /// <param name="interfaceClass">The interface class, for example <see cref="ComPortInterface" />.</param>
    /// <returns>Every interface path; empty when none is present.</returns>
    /// <exception cref="InvalidOperationException">Windows could not list them.</exception>
    public static unsafe string[] ListInterfaces(Guid interfaceClass)
    {
        // The list can grow between the size and the read when a device arrives; another attempt covers that.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var sized = CM_Get_Device_Interface_List_Size(out var length, in interfaceClass, 0, 0);
            if (sized != CrSuccess)
            {
                throw new InvalidOperationException($"CM_Get_Device_Interface_List_Size failed with {sized}.");
            }

            if (length < 2)
            {
                return [];
            }

            var buffer = new char[length];
            int result;
            fixed (char* pointer = buffer)
            {
                result = CM_Get_Device_Interface_List(in interfaceClass, 0, pointer, length, 0);
            }

            if (result == CrSuccess)
            {
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }

            if (result != CrBufferSmall)
            {
                throw new InvalidOperationException($"CM_Get_Device_Interface_List failed with {result}.");
            }
        }

        throw new InvalidOperationException("The device interface list kept changing while it was read.");
    }

    /// <summary>Reads a numeric PROPVARIANT, or NaN for anything that is not a number.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The number.</returns>
    public static double Numeric(in PropVariant value)
    {
        return value.VariantType switch
        {
            2 => value.Int16,
            3 => value.Int32,
            4 => value.Single,
            5 => value.Double,
            11 => value.Int16 != 0 ? 1 : 0,
            16 => value.SByte,
            17 => value.Byte,
            18 => value.UInt16,
            19 => value.UInt32,
            20 => value.Int64,
            21 => value.UInt64,
            _ => double.NaN
        };
    }

    /// <summary>Releases a COM object, ignoring null and non-COM values.</summary>
    /// <param name="value">The object.</param>
    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropVariant value);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    internal static partial int CM_Get_Device_Interface_List_Size(
        out uint length, in Guid interfaceClassGuid, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    internal static unsafe partial int CM_Get_Device_Interface_List(
        in Guid interfaceClassGuid, nint deviceId, char* buffer, uint bufferLength, uint flags);

    // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION waits in about a millisecond without raising the
    // machine-wide timer resolution.
    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    internal static partial SafeWaitHandle CreateWaitableTimerEx(
        nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimer(
        SafeWaitHandle timer, in long dueTime, int period, nint completion, nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(
        SafeFileHandle file, byte* buffer, uint length, out uint read, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(SafeFileHandle file, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCommState(SafeFileHandle file, ref Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCommState(SafeFileHandle file, in Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCommTimeouts(SafeFileHandle file, in CommTimeouts timeouts);

    /// <summary>DCB, the serial line settings.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Dcb
    {
        /// <summary>Structure size.</summary>
        public int Length;

        /// <summary>Baud rate.</summary>
        public uint BaudRate;

        /// <summary>fBinary, fParity, flow control, fDtrControl (bits 4-5), fRtsControl (bits 12-13) and the rest.</summary>
        public uint Flags;

        /// <summary>Reserved.</summary>
        public ushort Reserved;

        /// <summary>XON threshold.</summary>
        public ushort XonLimit;

        /// <summary>XOFF threshold.</summary>
        public ushort XoffLimit;

        /// <summary>Data bits.</summary>
        public byte ByteSize;

        /// <summary>Parity (0 none).</summary>
        public byte Parity;

        /// <summary>Stop bits (0 one).</summary>
        public byte StopBits;

        /// <summary>XON character.</summary>
        public byte XonChar;

        /// <summary>XOFF character.</summary>
        public byte XoffChar;

        /// <summary>Error character.</summary>
        public byte ErrorChar;

        /// <summary>End-of-input character.</summary>
        public byte EofChar;

        /// <summary>Event character.</summary>
        public byte EventChar;

        /// <summary>Reserved.</summary>
        public ushort Reserved1;
    }

    /// <summary>COMMTIMEOUTS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CommTimeouts
    {
        /// <summary>Largest gap between two bytes.</summary>
        public uint ReadIntervalTimeout;

        /// <summary>Per-byte read timeout.</summary>
        public uint ReadTotalTimeoutMultiplier;

        /// <summary>Fixed read timeout.</summary>
        public uint ReadTotalTimeoutConstant;

        /// <summary>Per-byte write timeout.</summary>
        public uint WriteTotalTimeoutMultiplier;

        /// <summary>Fixed write timeout.</summary>
        public uint WriteTotalTimeoutConstant;
    }

    /// <summary>PROPERTYKEY.</summary>
    /// <param name="formatId">Property-set GUID in the Windows PROPERTYKEY layout.</param>
    /// <param name="propertyId">Property identifier within that set.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        /// <summary>Format ID.</summary>
        public Guid FormatId = formatId;

        /// <summary>Property ID.</summary>
        public uint PropertyId = propertyId;

        /// <inheritdoc />
        public readonly override string ToString()
        {
            return $"{FormatId:D}:{PropertyId}";
        }
    }

    /// <summary>PROPVARIANT, scalar members only.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        /// <summary>VARTYPE.</summary>
        [FieldOffset(0)] public ushort VariantType;

        /// <summary>VT_I1.</summary>
        [FieldOffset(8)] public sbyte SByte;

        /// <summary>VT_UI1.</summary>
        [FieldOffset(8)] public byte Byte;

        /// <summary>VT_I2 and VT_BOOL.</summary>
        [FieldOffset(8)] public short Int16;

        /// <summary>VT_UI2.</summary>
        [FieldOffset(8)] public ushort UInt16;

        /// <summary>VT_I4.</summary>
        [FieldOffset(8)] public int Int32;

        /// <summary>VT_UI4.</summary>
        [FieldOffset(8)] public uint UInt32;

        /// <summary>VT_I8.</summary>
        [FieldOffset(8)] public long Int64;

        /// <summary>VT_UI8.</summary>
        [FieldOffset(8)] public ulong UInt64;

        /// <summary>VT_R4.</summary>
        [FieldOffset(8)] public float Single;

        /// <summary>VT_R8.</summary>
        [FieldOffset(8)] public double Double;

        /// <summary>VT_LPWSTR.</summary>
        [FieldOffset(8)] public nint Pointer;
    }

    /// <summary>SYSTEMTIME.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemTime
    {
        /// <summary>Year.</summary>
        public ushort Year;

        /// <summary>Month.</summary>
        public ushort Month;

        /// <summary>Day of the week.</summary>
        public ushort DayOfWeek;

        /// <summary>Day.</summary>
        public ushort Day;

        /// <summary>Hour.</summary>
        public ushort Hour;

        /// <summary>Minute.</summary>
        public ushort Minute;

        /// <summary>Second.</summary>
        public ushort Second;

        /// <summary>Milliseconds.</summary>
        public ushort Milliseconds;

        /// <summary>Converts to UTC ticks; false for a time that is not a valid date.</summary>
        /// <param name="ticks">UTC ticks.</param>
        /// <returns>Whether the time is valid.</returns>
        public readonly bool TryGetTicks(out long ticks)
        {
            ticks = 0;
            if (Year < 2000 || Month is < 1 or > 12 || Day < 1 || Day > DateTime.DaysInMonth(Year, Month)
                || Hour > 23 || Minute > 59 || Second > 59 || Milliseconds > 999)
            {
                return false;
            }

            ticks = new DateTime(Year, Month, Day, Hour, Minute, Second, Milliseconds, DateTimeKind.Utc).Ticks;
            return true;
        }
    }

    /// <summary>ISensorManager.</summary>
    [ComImport]
    [Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensorManager
    {
        [PreserveSig]
        int GetSensorsByCategory([In] ref Guid category, out ISensorCollection? sensors);

        [PreserveSig]
        int GetSensorsByType([In] ref Guid type, out ISensorCollection? sensors);

        [PreserveSig]
        int GetSensorByID([In] ref Guid id, out ISensor? sensor);

        [PreserveSig]
        int SetEventSink(nint events);

        [PreserveSig]
        int RequestPermissions(nint window, ISensorCollection sensors, [MarshalAs(UnmanagedType.Bool)] bool modal);
    }

    /// <summary>ISensorCollection.</summary>
    [ComImport]
    [Guid("23571E11-E545-4DD8-A337-B89BF44B10DF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensorCollection
    {
        [PreserveSig]
        int GetAt(uint index, out ISensor? sensor);

        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Add(ISensor sensor);

        [PreserveSig]
        int Remove(ISensor sensor);

        [PreserveSig]
        int RemoveByID([In] ref Guid id);

        [PreserveSig]
        int Clear();
    }

    /// <summary>ISensor.</summary>
    [ComImport]
    [Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensor
    {
        [PreserveSig]
        int GetID(out Guid id);

        [PreserveSig]
        int GetCategory(out Guid category);

        [PreserveSig]
        int GetType(out Guid type);

        [PreserveSig]
        int GetFriendlyName([MarshalAs(UnmanagedType.BStr)] out string? name);

        [PreserveSig]
        int GetProperty([In] ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int GetProperties(nint keys, out nint values);

        [PreserveSig]
        int GetSupportedDataFields(out IPortableDeviceKeyCollection? keys);

        [PreserveSig]
        int SetProperties(IPortableDeviceValues properties, out IPortableDeviceValues? results);

        [PreserveSig]
        int SupportsDataField([In] ref PropertyKey key, out short supported);

        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetData(out ISensorDataReport? report);

        [PreserveSig]
        int SupportsEvent([In] ref Guid eventGuid, out short supported);

        [PreserveSig]
        int GetEventInterest(out nint values, out uint count);

        [PreserveSig]
        int SetEventInterest(nint values, uint count);

        [PreserveSig]
        int SetEventSink(nint events);
    }

    /// <summary>ISensorDataReport.</summary>
    [ComImport]
    [Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISensorDataReport
    {
        [PreserveSig]
        int GetTimestamp(out SystemTime time);

        [PreserveSig]
        int GetSensorValue([In] ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int GetSensorValues(nint keys, out nint values);
    }

    /// <summary>IPortableDeviceKeyCollection.</summary>
    [ComImport]
    [Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceKeyCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, ref PropertyKey key);

        [PreserveSig]
        int Add([In] ref PropertyKey key);

        [PreserveSig]
        int Clear();

        [PreserveSig]
        int RemoveAt(uint index);
    }

    /// <summary>IPortableDeviceValues, up to the members the stage uses.</summary>
    [ComImport]
    [Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceValues
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int SetValue([In] ref PropertyKey key, [In] ref PropVariant value);

        [PreserveSig]
        int GetValue([In] ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetStringValue([In] ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);

        [PreserveSig]
        int GetStringValue([In] ref PropertyKey key, out nint value);

        [PreserveSig]
        int SetUnsignedIntegerValue([In] ref PropertyKey key, uint value);
    }

    /// <summary>SensorManager coclass.</summary>
    [ComImport]
    [Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
    internal class SensorManagerClass;

    /// <summary>PortableDeviceValues coclass.</summary>
    [ComImport]
    [Guid("0C15D503-D017-47CE-9016-7B3F978721CC")]
    internal class PortableDeviceValuesClass;
}
