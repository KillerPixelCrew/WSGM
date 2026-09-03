<#
.SYNOPSIS
Lists the legacy Windows Sensor API (sensorsapi, COM) sensors and reads the motion fields.

.DESCRIPTION
Read-only observation. WinRT's Windows.Devices.Sensors projects only sensors of the standard
types: on the MSI Claw 8 AI+ A2VM the Intel Sensor Hub's accelerometer is absent from WinRT
because it is exposed as a custom sensor (SENSOR_TYPE_CUSTOM) named "Physical Accelerometer"
that reports its three axes as SENSOR_DATA_TYPE_CUSTOM_VALUE1..3 (format GUID
b14c764f-07cf-41e8-9d82-ebe3d0776a6f, property ids 7, 8, 9 in sensors.h) rather than the
standard SENSOR_DATA_TYPE_ACCELERATION_*_G fields. Handheld Companion 1.2.1.1 reads it exactly
that way (Resources\Devices\ClawA2VM.json, IMUWindowsAccelerometer).

For every sensor this prints the friendly name, type, state, manufacturer, model, device path,
HID usage and supported data fields; the device path names the driver stack the sensor hangs off.
For every sensor whose name mentions an accelerometer or gyrometer it then polls GetData a few
times and prints the standard motion fields and the custom fields, whichever the sensor supports,
with the raw PROPVARIANT type so units can be judged (about 1.0 on one axis at rest means g; about
9.81 means m/s²).

Nothing is written, configured, or subscribed to. Sensor permission prompts are not requested.
Every COM call lives in the embedded C#; PowerShell's COM binder cannot reach IUnknown-only
interfaces itself.

.PARAMETER Samples
How many GetData polls to take per motion sensor. Defaults to 5.

.PARAMETER IntervalMs
Pause between polls in milliseconds. Defaults to 100.

.PARAMETER HidReadTimeoutMs
How long to wait for one input report from each HID sensor collection. Defaults to 1500.

.PARAMETER AllHid
List every HID collection's value capabilities, not only usage page 0x20. Reads are still
attempted only on sensor collections.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Samples = 5,

    [ValidateRange(10, 5000)]
    [int]$IntervalMs = 100,

    [ValidateRange(100, 10000)]
    [int]$HidReadTimeoutMs = 1500,

    [switch]$AllHid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LegacySensors
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid f, uint p) { fmtid = f; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
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
    public struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [ComImport, Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISensorManager
    {
        [PreserveSig] int GetSensorsByCategory([In] ref Guid category, out ISensorCollection sensors);
        [PreserveSig] int GetSensorsByType([In] ref Guid type, out ISensorCollection sensors);
        [PreserveSig] int GetSensorByID([In] ref Guid id, out ISensor sensor);
        [PreserveSig] int SetEventSink(IntPtr events);
        [PreserveSig] int RequestPermissions(IntPtr hwnd, ISensorCollection sensors, [MarshalAs(UnmanagedType.Bool)] bool modal);
    }

    [ComImport, Guid("23571E11-E545-4DD8-A337-B89BF44B10DF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISensorCollection
    {
        [PreserveSig] int GetAt(uint index, out ISensor sensor);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Add(ISensor sensor);
        [PreserveSig] int Remove(ISensor sensor);
        [PreserveSig] int RemoveByID([In] ref Guid id);
        [PreserveSig] int Clear();
    }

    [ComImport, Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISensor
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
    public interface ISensorDataReport
    {
        [PreserveSig] int GetTimestamp(out SYSTEMTIME time);
        [PreserveSig] int GetSensorValue([In] ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int GetSensorValues(IntPtr keys, out IntPtr values);
    }

    [ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPortableDeviceKeyCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref PROPERTYKEY key);
        [PreserveSig] int Add([In] ref PROPERTYKEY key);
        [PreserveSig] int Clear();
        [PreserveSig] int RemoveAt(uint index);
    }

    [ComImport, Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
    public class SensorManagerClass { }

    public static class Probe
    {
        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pvar);

        static readonly Guid CategoryAll = new Guid("C317C286-C468-4288-9975-D4C4587C442C");
        static readonly Guid MotionFormat = new Guid("3F8A69A2-07C5-4E48-A965-CD797AAB56D5");
        // SENSOR_DATA_TYPE_CUSTOM_GUID from sensors.h: pid 5 is CUSTOM_USAGE, 7.. are CUSTOM_VALUE1..28.
        static readonly Guid CustomFormat = new Guid("B14C764F-07CF-41E8-9D82-EBE3D0776A6F");
        // SENSOR_PROPERTY_COMMON_GUID: 6 manufacturer, 7 model, 15 device path, 22 HID usage.
        static readonly Guid CommonProperties = new Guid("7F8383EC-D3EC-495C-A8CF-B8BBE85C2920");

        static readonly Dictionary<Guid, string> KnownTypes = new Dictionary<Guid, string>
        {
            { new Guid("C2FB0F5F-E2D2-4C78-BCD0-352A9582819D"), "SENSOR_TYPE_ACCELEROMETER_3D" },
            { new Guid("09485F5A-759E-42C2-BD4B-A349B75C8643"), "SENSOR_TYPE_GYROMETER_3D" },
            { new Guid("2C60F5F5-2B21-4D5A-9E7A-9A5DD7D5BA5F"), "SENSOR_TYPE_INCLINOMETER_3D" },
            { new Guid("CDB5D8F7-3CFD-41C8-8542-CCE622CF5D6E"), "SENSOR_TYPE_AGGREGATED_DEVICE_ORIENTATION" },
            { new Guid("97F115C8-599A-4153-8894-D2D12899918A"), "SENSOR_TYPE_AMBIENT_LIGHT" },
            { new Guid("E83AF229-8640-4D18-A213-E22675EBB2C3"), "SENSOR_TYPE_CUSTOM — invisible to WinRT Accelerometer/Gyrometer" },
        };

        static readonly string[] States = { "Ready", "NotAvailable", "NoData", "Initializing", "AccessDenied", "Error" };

        static string ReadField(ISensorDataReport report, Guid format, uint id)
        {
            PROPERTYKEY key = new PROPERTYKEY(format, id);
            PROPVARIANT value;
            int hr = report.GetSensorValue(ref key, out value);
            if (hr < 0) return "hr=0x" + hr.ToString("X8");
            string text = value.Describe();
            PropVariantClear(ref value);
            return text;
        }

        static string ReadProperty(ISensor sensor, uint id)
        {
            PROPERTYKEY key = new PROPERTYKEY(CommonProperties, id);
            PROPVARIANT value;
            int hr = sensor.GetProperty(ref key, out value);
            if (hr < 0) return "hr=0x" + hr.ToString("X8");
            string text = value.Describe();
            PropVariantClear(ref value);
            return text;
        }

        static string FieldLabel(PROPERTYKEY key)
        {
            if (key.fmtid == MotionFormat) return " (standard motion format)";
            if (key.fmtid == CustomFormat)
            {
                if (key.pid == 5) return " (SENSOR_DATA_TYPE_CUSTOM_USAGE)";
                if (key.pid == 6) return " (SENSOR_DATA_TYPE_CUSTOM_BOOLEAN_ARRAY)";
                if (key.pid >= 7 && key.pid <= 34) return " (SENSOR_DATA_TYPE_CUSTOM_VALUE" + (key.pid - 6) + ")";
            }
            return "";
        }

        public static string[] Run(int samples, int intervalMs)
        {
            List<string> lines = new List<string>();
            ISensorManager manager;
            try
            {
                manager = (ISensorManager)new SensorManagerClass();
            }
            catch (Exception ex)
            {
                lines.Add("Legacy Sensor Manager unavailable: " + ex.GetType().Name + ": " + ex.Message);
                return lines.ToArray();
            }

            Guid category = CategoryAll;
            ISensorCollection collection;
            int hr = manager.GetSensorsByCategory(ref category, out collection);
            if (hr < 0 || collection == null)
            {
                lines.Add("No legacy sensors: GetSensorsByCategory returned 0x" + hr.ToString("X8") + ".");
                return lines.ToArray();
            }

            uint count;
            collection.GetCount(out count);
            lines.Add("Legacy sensors: " + count);

            for (uint index = 0; index < count; index++)
            {
                ISensor sensor;
                if (collection.GetAt(index, out sensor) < 0 || sensor == null) continue;

                string name; sensor.GetFriendlyName(out name);
                Guid type; sensor.GetType(out type);
                string typeName; if (!KnownTypes.TryGetValue(type, out typeName)) typeName = "unknown type";
                int state; sensor.GetState(out state);
                string stateName = state >= 0 && state < States.Length ? States[state] : "state " + state;

                lines.Add("");
                lines.Add("[" + index + "] " + name);
                lines.Add("    type  " + type + " (" + typeName + ")");
                lines.Add("    state " + stateName);
                lines.Add("    manufacturer " + ReadProperty(sensor, 6) + "; model " + ReadProperty(sensor, 7));
                lines.Add("    device path  " + ReadProperty(sensor, 15));
                lines.Add("    HID usage    " + ReadProperty(sensor, 22));

                IPortableDeviceKeyCollection keys;
                if (sensor.GetSupportedDataFields(out keys) >= 0 && keys != null)
                {
                    uint keyCount; keys.GetCount(out keyCount);
                    for (uint k = 0; k < keyCount; k++)
                    {
                        PROPERTYKEY key = new PROPERTYKEY();
                        if (keys.GetAt(k, ref key) < 0) continue;
                        lines.Add("    field " + key.fmtid + " pid " + key.pid + FieldLabel(key));
                    }
                }

                string lower = (name ?? "").ToLowerInvariant();
                bool accel = lower.Contains("accel");
                bool gyro = lower.Contains("gyro");
                if (!accel && !gyro) continue;

                for (int sample = 1; sample <= samples; sample++)
                {
                    ISensorDataReport report;
                    hr = sensor.GetData(out report);
                    if (hr < 0 || report == null)
                    {
                        lines.Add("    sample " + sample + ": GetData failed 0x" + hr.ToString("X8"));
                        Thread.Sleep(intervalMs);
                        continue;
                    }
                    SYSTEMTIME time; report.GetTimestamp(out time);
                    string stamp = string.Format("{0:00}:{1:00}:{2:00}.{3:000}", time.Hour, time.Minute, time.Second, time.Milliseconds);
                    string standard = accel
                        ? ReadField(report, MotionFormat, 2) + " | " + ReadField(report, MotionFormat, 3) + " | " + ReadField(report, MotionFormat, 4)
                        : ReadField(report, MotionFormat, 10) + " | " + ReadField(report, MotionFormat, 11) + " | " + ReadField(report, MotionFormat, 12);
                    string custom = ReadField(report, CustomFormat, 7) + " | " + ReadField(report, CustomFormat, 8) + " | " + ReadField(report, CustomFormat, 9);
                    lines.Add("    sample " + sample + " at " + stamp);
                    lines.Add("      standard XYZ:     " + standard);
                    lines.Add("      custom value1..3: " + custom);
                    Thread.Sleep(intervalMs);
                }
            }
            return lines.ToArray();
        }
    }

    // One layer below the Sensor API: the HID sensor collections themselves (usage page 0x20),
    // as the ISH HID transport presents them before the HID sensor class driver interprets them.
    // Enumeration and capability parsing are pure reads. The open is read-only and shared; one
    // bounded input-report read follows, and the handle is closed to cancel it on timeout.
    public static class HidProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public UIntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Explicit, Size = 72)]
        struct HIDP_VALUE_CAPS
        {
            [FieldOffset(0)] public ushort UsagePage;
            [FieldOffset(2)] public byte ReportID;
            [FieldOffset(6)] public ushort LinkCollection;
            [FieldOffset(12)] public byte IsRange;
            [FieldOffset(18)] public ushort BitSize;
            [FieldOffset(20)] public ushort ReportCount;
            [FieldOffset(32)] public uint UnitsExp;
            [FieldOffset(36)] public uint Units;
            [FieldOffset(40)] public int LogicalMin;
            [FieldOffset(44)] public int LogicalMax;
            [FieldOffset(48)] public int PhysicalMin;
            [FieldOffset(52)] public int PhysicalMax;
            [FieldOffset(56)] public ushort UsageMin;
            [FieldOffset(58)] public ushort UsageMax;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsed);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(IntPtr device, byte[] buffer, uint length);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
        [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr preparsed);
        [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr infoSet, IntPtr infoData, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr infoSet, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr infoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(IntPtr handle, byte[] buffer, uint count, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

        const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
        const int HidP_Input = 0;
        static readonly IntPtr INVALID = new IntPtr(-1);

        static string UnitName(uint units, uint exp)
        {
            // HID sensor accelerometers declare SI linear (0x11) with metres and seconds; HID motion
            // usages in g are declared unitless with a unit exponent. The nibble layout is the
            // HID 1.11 unit encoding; this labels the two shapes that matter here.
            int e = (int)(exp & 0xF); if (e > 7) e -= 16;
            if (units == 0) return "unitless, exponent 10^" + e;
            if ((units & 0xF) == 1) return "SI linear 0x" + units.ToString("X") + ", exponent 10^" + e;
            return "units 0x" + units.ToString("X") + ", exponent 10^" + e;
        }

        public static string[] Run(bool allCollections, int readTimeoutMs)
        {
            List<string> lines = new List<string>();
            Guid hidGuid; HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID) { lines.Add("HID enumeration failed: " + Marshal.GetLastWin32Error()); return lines.ToArray(); }
            int shown = 0;
            try
            {
                for (uint index = 0; ; index++)
                {
                    SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data)) break;
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero)) continue;
                        path = Marshal.PtrToStringUni(detail + 4);
                    }
                    finally { Marshal.FreeHGlobal(detail); }

                    // Capability parsing needs no read access: open with no access rights first.
                    IntPtr meta = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (meta == INVALID) continue;
                    HIDD_ATTRIBUTES attributes = new HIDD_ATTRIBUTES(); attributes.Size = (uint)Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                    HidD_GetAttributes(meta, ref attributes);
                    IntPtr preparsed;
                    HIDP_CAPS caps = new HIDP_CAPS();
                    bool parsed = HidD_GetPreparsedData(meta, out preparsed) && HidP_GetCaps(preparsed, out caps) == 0x110000;
                    string product = "";
                    byte[] name = new byte[256];
                    if (HidD_GetProductString(meta, name, (uint)name.Length)) product = System.Text.Encoding.Unicode.GetString(name).TrimEnd('\0');
                    CloseHandle(meta);
                    if (!parsed) continue;
                    bool sensorPage = caps.UsagePage == 0x20;
                    if (!sensorPage && !allCollections) { HidD_FreePreparsedData(preparsed); continue; }
                    shown++;
                    lines.Add("");
                    lines.Add(string.Format("HID VID_{0:X4}&PID_{1:X4} usage 0x{2:X2}:0x{3:X4} \"{4}\"", attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage, product));
                    lines.Add("    path " + path);
                    lines.Add(string.Format("    input report {0} bytes, {1} input value caps, feature report {2} bytes", caps.InputReportByteLength, caps.NumberInputValueCaps, caps.FeatureReportByteLength));

                    ushort count = caps.NumberInputValueCaps;
                    HIDP_VALUE_CAPS[] values = new HIDP_VALUE_CAPS[Math.Max(count, (ushort)1)];
                    if (count > 0 && HidP_GetValueCaps(HidP_Input, values, ref count, preparsed) == 0x110000)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            HIDP_VALUE_CAPS v = values[i];
                            string usage = v.IsRange != 0 ? string.Format("0x{0:X4}..0x{1:X4}", v.UsageMin, v.UsageMax) : string.Format("0x{0:X4}", v.UsageMin);
                            lines.Add(string.Format("    value report {0} page 0x{1:X2} usage {2}: {3} bits x{4}, logical {5}..{6}, physical {7}..{8}, {9}",
                                v.ReportID, v.UsagePage, usage, v.BitSize, v.ReportCount, v.LogicalMin, v.LogicalMax, v.PhysicalMin, v.PhysicalMax, UnitName(v.Units, v.UnitsExp)));
                        }
                    }

                    if (!sensorPage || caps.InputReportByteLength == 0) { HidD_FreePreparsedData(preparsed); continue; }
                    IntPtr handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (handle == INVALID)
                    {
                        int error = Marshal.GetLastWin32Error();
                        lines.Add("    read-only open refused: Win32 error " + error + (error == 5 ? " (access denied: the class driver owns the collection)" : error == 32 ? " (sharing violation)" : ""));
                        HidD_FreePreparsedData(preparsed);
                        continue;
                    }
                    byte[] report = new byte[caps.InputReportByteLength];
                    uint read = 0; bool ok = false; int readError = 0;
                    Thread reader = new Thread(() => { ok = ReadFile(handle, report, (uint)report.Length, out read, IntPtr.Zero); if (!ok) readError = Marshal.GetLastWin32Error(); });
                    reader.IsBackground = true; reader.Start();
                    if (!reader.Join(readTimeoutMs))
                    {
                        CloseHandle(handle); reader.Join(2000);
                        lines.Add("    read-only open succeeded; no input report within " + readTimeoutMs + " ms (the collection may be idle until a sensor client enables reporting)");
                    }
                    else
                    {
                        CloseHandle(handle);
                        if (!ok) lines.Add("    read-only open succeeded; ReadFile failed: Win32 error " + readError);
                        else
                        {
                            int show = (int)Math.Min(read, 48u);
                            lines.Add("    input report read: " + read + " bytes, id " + report[0] + ": " + BitConverter.ToString(report, 0, show).Replace("-", " ") + (read > show ? " …" : ""));
                            if (count > 0)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    HIDP_VALUE_CAPS v = values[i];
                                    if (v.IsRange != 0 || v.ReportID != report[0]) continue;
                                    uint value;
                                    if (HidP_GetUsageValue(HidP_Input, v.UsagePage, v.LinkCollection, v.UsageMin, out value, preparsed, report, read) == 0x110000)
                                    {
                                        long signed = v.BitSize < 32 && (value & (1u << (v.BitSize - 1))) != 0 ? (long)value - (1L << v.BitSize) : value;
                                        lines.Add(string.Format("      usage 0x{0:X4} = {1} (raw 0x{2:X})", v.UsageMin, signed, value));
                                    }
                                }
                            }
                        }
                    }
                    HidD_FreePreparsedData(preparsed);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            if (shown == 0) lines.Add(allCollections ? "No HID collections could be parsed." : "No HID collections on usage page 0x20 (Sensors).");
            return lines.ToArray();
        }
    }
}
'@

Write-Output '=== Sensor API (sensorsapi, what Handheld Companion reads) ==='
[LegacySensors.Probe]::Run($Samples, $IntervalMs) | ForEach-Object { Write-Output $_ }
Write-Output ''
Write-Output '=== HID sensor collections (one layer below: the ISH HID transport) ==='
[LegacySensors.HidProbe]::Run([bool]$AllHid, $HidReadTimeoutMs) | ForEach-Object { Write-Output $_ }
