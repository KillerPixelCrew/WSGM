<#
.SYNOPSIS
Lists the legacy Windows Sensor API (sensorsapi, COM) sensors and reads the motion fields.

.DESCRIPTION
Read-only observation. WinRT's Windows.Devices.Sensors projects only some of what the Sensor and
Location platform exposes: on the MSI Claw 8 AI+ A2VM the Intel Sensor Hub's accelerometer is
absent from WinRT but present here as a sensor named "Physical Accelerometer" that reports its
three axes under a vendor data format (GUID b14c764f-07cf-41e8-9d82-ebe3d0776a6f, property ids
7, 8, 9) rather than the standard SENSOR_DATA_TYPE_ACCELERATION_*_G fields. Handheld Companion
1.2.1.1 reads it exactly that way (Resources\Devices\ClawA2VM.json, IMUWindowsAccelerometer).

For every sensor this prints the friendly name, type, state and supported data fields. For every
sensor whose name mentions an accelerometer or gyrometer it then polls GetData a few times and
prints the standard motion fields and the vendor fields, whichever the sensor supports, with the
raw PROPVARIANT type so units can be judged (about 1.0 on one axis at rest means g; about 9.81
means m/s²).

Nothing is written, configured, or subscribed to. Sensor permission prompts are not requested.
Every COM call lives in the embedded C#; PowerShell's COM binder cannot reach IUnknown-only
interfaces itself.

.PARAMETER Samples
How many GetData polls to take per motion sensor. Defaults to 5.

.PARAMETER IntervalMs
Pause between polls in milliseconds. Defaults to 100.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Samples = 5,

    [ValidateRange(10, 5000)]
    [int]$IntervalMs = 100
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
        static readonly Guid CategoryAll = new Guid("C317C286-C468-4288-9975-D4C4587C442C");
        static readonly Guid MotionFormat = new Guid("3F8A69A2-07C5-4E48-A965-CD797AAB56D5");
        static readonly Guid VendorFormat = new Guid("B14C764F-07CF-41E8-9D82-EBE3D0776A6F");

        static readonly Dictionary<Guid, string> KnownTypes = new Dictionary<Guid, string>
        {
            { new Guid("C2FB0F5F-E2D2-4C78-BCD0-352A9582819D"), "SENSOR_TYPE_ACCELEROMETER_3D" },
            { new Guid("09485F5A-759E-42C2-BD4B-A349B75C8643"), "SENSOR_TYPE_GYROMETER_3D" },
            { new Guid("2C60F5F5-2B21-4D5A-9E7A-9A5DD7D5BA5F"), "SENSOR_TYPE_INCLINOMETER_3D" },
            { new Guid("CDB5D8F7-3CFD-41C8-8542-CCE622CF5D6E"), "SENSOR_TYPE_AGGREGATED_DEVICE_ORIENTATION" },
            { new Guid("97F115C8-599A-4153-8894-D2D12899918A"), "SENSOR_TYPE_AMBIENT_LIGHT" },
        };

        static readonly string[] States = { "Ready", "NotAvailable", "NoData", "Initializing", "AccessDenied", "Error" };

        static string ReadField(ISensorDataReport report, Guid format, uint id)
        {
            PROPERTYKEY key = new PROPERTYKEY(format, id);
            PROPVARIANT value;
            int hr = report.GetSensorValue(ref key, out value);
            if (hr < 0) return "hr=0x" + hr.ToString("X8");
            return value.Describe();
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

                IPortableDeviceKeyCollection keys;
                if (sensor.GetSupportedDataFields(out keys) >= 0 && keys != null)
                {
                    uint keyCount; keys.GetCount(out keyCount);
                    for (uint k = 0; k < keyCount; k++)
                    {
                        PROPERTYKEY key = new PROPERTYKEY();
                        if (keys.GetAt(k, ref key) < 0) continue;
                        string label = "";
                        if (key.fmtid == MotionFormat) label = " (standard motion format)";
                        else if (key.fmtid == VendorFormat) label = " (vendor format Handheld Companion reads)";
                        lines.Add("    field " + key.fmtid + " pid " + key.pid + label);
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
                    string vendor = ReadField(report, VendorFormat, 7) + " | " + ReadField(report, VendorFormat, 8) + " | " + ReadField(report, VendorFormat, 9);
                    lines.Add("    sample " + sample + " at " + stamp);
                    lines.Add("      standard XYZ: " + standard);
                    lines.Add("      vendor 7/8/9: " + vendor);
                    Thread.Sleep(intervalMs);
                }
            }
            return lines.ToArray();
        }
    }
}
'@

[LegacySensors.Probe]::Run($Samples, $IntervalMs) | ForEach-Object { Write-Output $_ }
