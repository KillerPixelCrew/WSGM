// Sensor COM ABI reused from tools/probe-legacy-sensors.ps1.
using System.Runtime.InteropServices;
namespace WSGM.AllyXLab;

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
    public PROPERTYKEY(Guid f, uint p) { fmtid = f; pid = p; }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public sbyte i1;
    [FieldOffset(8)] public byte ui1;
    [FieldOffset(8)] public short i2;
    [FieldOffset(8)] public ushort ui2;
    [FieldOffset(8)] public int i4;
    [FieldOffset(8)] public uint ui4;
    [FieldOffset(8)] public long i8;
    [FieldOffset(8)] public ulong ui8;
    [FieldOffset(8)] public float r4;
    [FieldOffset(8)] public double r8;
    [FieldOffset(8)] public IntPtr ptr;

    public string Describe()
    {
        switch (vt)
        {
            case 0: return "VT_EMPTY";
            case 2: return "VT_I2 " + i2;
            case 3: return "VT_I4 " + i4;
            case 4: return "VT_R4 " + r4.ToString("R");
            case 5: return "VT_R8 " + r8.ToString("R");
            case 11: return "VT_BOOL " + (i2 != 0);
            case 16: return "VT_I1 " + i1;
            case 17: return "VT_UI1 " + ui1;
            case 18: return "VT_UI2 " + ui2;
            case 19: return "VT_UI4 " + ui4;
            case 20: return "VT_I8 " + i8;
            case 21: return "VT_UI8 " + ui8;
            case 31: return "VT_LPWSTR " + Marshal.PtrToStringUni(ptr);
            default: return "vt=" + vt;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEMTIME
{
    public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
}

[ComImport, Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISensorManager
{
    [PreserveSig] int GetSensorsByCategory([In] ref Guid category, out ISensorCollection sensors);
    [PreserveSig] int GetSensorsByType([In] ref Guid type, out ISensorCollection sensors);
    [PreserveSig] int GetSensorByID([In] ref Guid id, out ISensor sensor);
    [PreserveSig] int SetEventSink(IntPtr events);
    [PreserveSig] int RequestPermissions(IntPtr hwnd, ISensorCollection sensors, [MarshalAs(UnmanagedType.Bool)] bool modal);
}

[ComImport, Guid("23571E11-E545-4DD8-A337-B89BF44B10DF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISensorCollection
{
    [PreserveSig] int GetAt(uint index, out ISensor sensor);
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Add(ISensor sensor);
    [PreserveSig] int Remove(ISensor sensor);
    [PreserveSig] int RemoveByID([In] ref Guid id);
    [PreserveSig] int Clear();
}

[ComImport, Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISensor
{
    [PreserveSig] int GetID(out Guid id);
    [PreserveSig] int GetCategory(out Guid category);
    [PreserveSig] int GetType(out Guid type);
    [PreserveSig] int GetFriendlyName([MarshalAs(UnmanagedType.BStr)] out string name);
    [PreserveSig] int GetProperty([In] ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int GetProperties(IntPtr keys, out IntPtr values);
    [PreserveSig] int GetSupportedDataFields(out IPortableDeviceKeyCollection keys);
    [PreserveSig] int SetProperties(IntPtr properties, out IntPtr results);
    [PreserveSig] int SupportsDataField([In] ref PROPERTYKEY key, out short supported);
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetData(out ISensorDataReport report);
    [PreserveSig] int SupportsEvent([In] ref Guid eventGuid, out short supported);
    [PreserveSig] int GetEventInterest(out IntPtr values, out uint count);
    [PreserveSig] int SetEventInterest(IntPtr values, uint count);
    [PreserveSig] int SetEventSink(IntPtr events);
}

[ComImport, Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISensorDataReport
{
    [PreserveSig] int GetTimestamp(out SYSTEMTIME time);
    [PreserveSig] int GetSensorValue([In] ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int GetSensorValues(IntPtr keys, out IntPtr values);
}

[ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, ref PROPERTYKEY key);
    [PreserveSig] int Add([In] ref PROPERTYKEY key);
    [PreserveSig] int Clear();
    [PreserveSig] int RemoveAt(uint index);
}

[ComImport, Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
internal class SensorManagerClass { }
