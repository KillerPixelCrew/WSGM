using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.NvidiaGpu;

/// <summary>Documented NVAPI DRS/color layouts; private output extensions live in NvApiPrivate.cs.</summary>
internal sealed unsafe partial class NvApi : INvProfiles, IDisposable
{
    internal const int SettingSize = 12320;
    internal const int SettingIdOffset = 4100;
    internal const int CurrentValueOffset = 8220;
    internal const int ValuesSize = 414112;
    internal const int ValueStride = 4100;
    internal const int ApplicationSize = 20492;
    internal const int ProfileSize = 4116;
    internal const int ColorSize = 24;
    private readonly delegate* unmanaged[Cdecl]<uint, nint> _query;
    private bool _initialized;
    private nint _library;

    internal NvApi()
    {
        _library = NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "nvapi64.dll"));
        try
        {
            _query = (delegate* unmanaged[Cdecl]<uint, nint>)NativeLibrary.GetExport(_library, "nvapi_QueryInterface");
            Check(((delegate* unmanaged[Cdecl]<int>)Function(0x0150e828))(), "Initialize");
            _initialized = true;
            nint session = 0;
            Check(((delegate* unmanaged[Cdecl]<nint*, int>)Function(0x0694d52e))(&session), "DRS CreateSession");
            Drs = session;
            Load();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint Drs { get; private set; }

    public void Dispose()
    {
        if (_library == 0)
        {
            return;
        }

        try
        {
            if (Drs != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, int>)Function(0xdad9cff8))(Drs);
                Drs = 0;
            }

            if (_initialized)
            {
                ((delegate* unmanaged[Cdecl]<int>)Function(0xd22bdd7e))();
                _initialized = false;
            }
        }
        finally
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    public void Load()
    {
        Check(((delegate* unmanaged[Cdecl]<nint, int>)Function(0x375dbd6b))(Drs), "DRS LoadSettings");
    }

    public void Save()
    {
        DriverWriteScope.Check();
        Check(((delegate* unmanaged[Cdecl]<nint, int>)Function(0xfcbc7e14))(Drs), "DRS SaveSettings", true);
    }

    public nint GlobalProfile()
    {
        nint profile = 0;
        Check(((delegate* unmanaged[Cdecl]<nint, nint*, int>)Function(0x617bff9f))(Drs, &profile),
            "DRS GetCurrentGlobalProfile");
        return profile;
    }

    public (uint Value, bool Explicit) Get(nint profile, uint setting)
    {
        var buffer = NewSetting(setting);
        fixed (byte* pointer = buffer)
        {
            Check(
                ((delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, int>)Function(0x73bf8338))(Drs, profile, setting,
                    pointer), "DRS GetSetting");
        }

        if (Number(buffer, 4104) != 0)
        {
            throw new DriverFailure("The setting is not a documented DWORD setting.");
        }

        return (Number(buffer, CurrentValueOffset), Number(buffer, 4108) == 0 && Number(buffer, 4112) == 0);
    }

    public void Set(nint profile, uint setting, uint value)
    {
        DriverWriteScope.Check();
        var buffer = NewSetting(setting);
        Number(buffer, CurrentValueOffset, value);
        fixed (byte* pointer = buffer)
        {
            Check(((delegate* unmanaged[Cdecl]<nint, nint, byte*, int>)Function(0x577dd202))(Drs, profile, pointer),
                "DRS SetSetting", true);
        }
    }

    public void Inherit(nint profile, uint setting)
    {
        DriverWriteScope.Check();
        // Delete this one user override. Predefined settings and every unrelated setting remain.
        var status = ((delegate* unmanaged[Cdecl]<nint, nint, uint, int>)Function(0xe4a26362))(Drs, profile, setting);
        if (status != -160)
        {
            Check(status, "DRS DeleteProfileSetting", true);
        }
    }

    public nint Application(string executable, bool create, string profileName)
    {
        if (executable.Length is 0 or > 260 || Path.GetFileName(executable) != executable
                                            || executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                                            executable.Any(PlainText.IsUnsafe)
                                            || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new DriverFailure("A native NVIDIA profile requires a plain executable filename.");
        }

        var app = new byte[ApplicationSize];
        Number(app, 0, Version(ApplicationSize, 4));
        Unicode(app, 8, executable);
        nint profile = 0;
        int status;
        fixed (char* name = executable)
        fixed (byte* pointer = app)
        {
            status = ((delegate* unmanaged[Cdecl]<nint, char*, nint*, byte*, int>)Function(0xeee566b2))(Drs, name,
                &profile, pointer);
        }

        if (status == 0)
        {
            return profile;
        }

        // NVAPI_EXECUTABLE_NOT_FOUND is the sole permission to create an application association.
        if (status == -166 && !create)
        {
            return 0;
        }

        if (status != -166)
        {
            Check(status, "DRS FindApplicationByName");
        }

        var info = new byte[ProfileSize];
        Number(info, 0, Version(ProfileSize, 1));
        Unicode(info, 4, profileName);
        fixed (char* name = profileName)
        {
            status = ((delegate* unmanaged[Cdecl]<nint, char*, nint*, int>)Function(0x7e4a9a0b))(Drs, name, &profile);
        }

        if (status == -163)
        {
            fixed (byte* pointer = info)
            {
                Check(
                    ((delegate* unmanaged[Cdecl]<nint, byte*, nint*, int>)Function(0xcc176068))(Drs, pointer, &profile),
                    "DRS CreateProfile", true);
            }
        }
        else
        {
            Check(status, "DRS FindProfileByName");
        }

        fixed (byte* pointer = app)
        {
            Check(((delegate* unmanaged[Cdecl]<nint, nint, byte*, int>)Function(0x4347a9de))(Drs, profile, pointer),
                "DRS CreateApplication", true);
        }

        return profile;
    }

    public string ProfileName(nint profile)
    {
        var info = new byte[ProfileSize];
        Number(info, 0, Version(ProfileSize, 1));
        fixed (byte* pointer = info)
        {
            Check(((delegate* unmanaged[Cdecl]<nint, nint, byte*, int>)Function(0x61cd6fd6))(Drs, profile, pointer),
                "DRS GetProfileInfo");
        }

        return Encoding.Unicode.GetString(info, 4, 4096).Split('\0')[0];
    }

    public nint FindProfile(string name)
    {
        nint profile = 0;
        int status;
        fixed (char* text = name)
        {
            status = ((delegate* unmanaged[Cdecl]<nint, char*, nint*, int>)Function(0x7e4a9a0b))(Drs, text, &profile);
        }

        if (status == -163)
        {
            return 0;
        }

        Check(status, "DRS FindProfileByName");
        return profile;
    }

    internal nint Function(uint id)
    {
        return _query(id) is var pointer && pointer != 0
            ? pointer
            : throw new DriverFailure($"NVAPI function 0x{id:X8} is unavailable.");
    }

    internal HashSet<uint> SettingIds()
    {
        uint count = 4096;
        var values = new uint[count];
        fixed (uint* pointer = values)
        {
            Check(((delegate* unmanaged[Cdecl]<uint*, uint*, int>)Function(0xf020614a))(pointer, &count),
                "DRS EnumAvailableSettingIds");
        }

        if (count > values.Length)
        {
            throw new DriverFailure("The NVIDIA setting table exceeds the supplied buffer.");
        }

        return values.Take((int)count).ToHashSet();
    }

    internal uint[] Values(uint setting)
    {
        var buffer = new byte[ValuesSize];
        Number(buffer, 0, Version(ValuesSize, 1));
        uint count = 100;
        fixed (byte* pointer = buffer)
        {
            Check(((delegate* unmanaged[Cdecl]<uint, uint*, byte*, int>)Function(0x2ec39f90))(setting, &count, pointer),
                "DRS EnumAvailableSettingValues");
        }

        if (Number(buffer, 8) != 0 || count > 100 || Number(buffer, 4) > 100)
        {
            throw new DriverFailure("The setting value table is unsupported.");
        }

        return Enumerable.Range(0, (int)Math.Min(count, Number(buffer, 4)))
            .Select(index => Number(buffer, 12 + ValueStride + index * ValueStride)).Distinct().ToArray();
    }

    internal NvOutput[] Displays()
    {
        var gpus = new nint[64];
        uint count = 0;
        fixed (nint* pointer = gpus)
        {
            Check(((delegate* unmanaged[Cdecl]<nint*, uint*, int>)Function(0xe5ac921f))(pointer, &count),
                "EnumPhysicalGPUs");
        }

        if (count is 0 or > 64)
        {
            throw new DriverFailure("No NVIDIA physical GPU is available.");
        }

        var displays = new Dictionary<uint, NvOutput>();
        foreach (var gpu in gpus.Take((int)count))
        {
            uint outputs = 0;
            var get = (delegate* unmanaged[Cdecl]<nint, byte*, uint*, uint, int>)Function(0x0078dba2);
            var status = get(gpu, null, &outputs, 1); // NV_GPU_CONNECTED_IDS_FLAG_UNCACHED
            if (status is -207 or -210 or -220) // no connector/display or an unpowered Optimus adapter
            {
                continue;
            }

            Check(status, "GetConnectedDisplayIds count");
            if (outputs is 0 or > 64)
            {
                continue;
            }

            var data = new byte[outputs * 16];
            for (var index = 0; index < outputs; index++)
            {
                Number(data, index * 16, Version(16, 3));
            }

            fixed (byte* pointer = data)
            {
                Check(get(gpu, pointer, &outputs, 1), "GetConnectedDisplayIds");
            }

            if (outputs > data.Length / 16)
            {
                throw new DriverFailure("The NVIDIA display topology changed during enumeration.");
            }

            for (var index = 0; index < outputs; index++)
            {
                if ((Number(data, index * 16 + 12) & 4) != 0)
                {
                    var display = Number(data, index * 16 + 8);
                    displays[display] = new NvOutput(display, gpu);
                }
            }
        }

        IReadOnlyList<ActiveDisplayPath> paths;
        try
        {
            paths = displays.Count == 0 ? [] : DisplayTopology.CaptureActive().Paths;
        }
        catch (Win32Exception)
        {
            // Driver-global profiles remain usable when Windows has no queryable display topology.
            paths = [];
        }

        foreach (var display in displays.Keys.ToArray())
        {
            var matches = paths.Where(path => DisplayId(path.SourceName) == display).ToArray();
            if (matches.Length == 1)
            {
                displays[display] = displays[display] with { Target = matches[0].Target };
            }
        }

        return displays.Values.OrderBy(output => output.Id).ToArray();
    }

    private uint? DisplayId(string sourceName)
    {
        var name = Encoding.ASCII.GetBytes(sourceName + '\0');
        uint id = 0;
        fixed (byte* pointer = name)
        {
            return ((delegate* unmanaged[Cdecl]<byte*, uint*, int>)Function(0xae457190))(pointer, &id) == 0 ? id : null;
        }
    }

    internal NvOutput RequireOutput(uint display)
    {
        return Displays().FirstOrDefault(output => output.Id == display)
               ?? throw new DriverFailure("The NVIDIA output was disconnected or moved to another adapter.");
    }

    internal NvOutput RequireOutput(NvOutput expected)
    {
        var current = RequireOutput(expected.Id);
        if (current.Gpu != expected.Gpu)
        {
            throw new DriverFailure("The NVIDIA adapter handles changed; its controls must be refreshed.", lost: true);
        }

        if (expected.Target is not null && (current.Target is null || !expected.Target.Matches(current.Target)))
        {
            throw new DriverFailure("The NVIDIA output's physical monitor changed before the write.");
        }

        return current;
    }

    internal uint Vrr(uint display)
    {
        var data = new byte[24];
        Number(data, 0, Version(data.Length, 1));
        fixed (byte* pointer = data)
        {
            Check(((delegate* unmanaged[Cdecl]<uint, byte*, int>)Function(0xdf8fda57))(display, pointer),
                "Disp_GetVRRInfo");
        }

        return Number(data, 4);
    }

    internal uint HdrCapabilities(uint display)
    {
        var data = new byte[64]; // NV_HDR_CAPABILITIES_V3, including both metadata blocks
        Number(data, 0, Version(data.Length, 3));
        fixed (byte* pointer = data)
        {
            Check(((delegate* unmanaged[Cdecl]<uint, byte*, int>)Function(0x84f2a8df))(display, pointer),
                "Disp_GetHdrCapabilities");
        }

        return Number(data, 4);
    }

    internal byte[] Color(uint display, byte command, byte[]? input = null)
    {
        var buffer = input is null ? new byte[ColorSize] : (byte[])input.Clone();
        Number(buffer, 0, Version(ColorSize, 5));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), ColorSize);
        buffer[6] = command;
        fixed (byte* pointer = buffer)
        {
            if (command == 2)
            {
                DriverWriteScope.Check();
            }

            Check(((delegate* unmanaged[Cdecl]<uint, byte*, int>)Function(0x92f9d80d))(display, pointer),
                "Disp_ColorControl", command == 2);
        }

        return buffer;
    }

    internal bool SupportsColor(uint display, byte[] input)
    {
        try
        {
            Color(display, 3, input);
            return true;
        }
        catch (DriverFailure failure) when (!failure.Lost)
        {
            return false;
        }
    }

    internal bool HasFunction(uint id)
    {
        return _query(id) != 0;
    }

    internal static uint Version(int size, int revision)
    {
        return (uint)size | ((uint)revision << 16);
    }

    internal static uint Number(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    internal static void Number(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }

    private static byte[] NewSetting(uint setting)
    {
        var data = new byte[SettingSize];
        Number(data, 0, Version(SettingSize, 1));
        Number(data, SettingIdOffset, setting);
        return data;
    }

    private static void Unicode(byte[] data, int offset, string value)
    {
        Encoding.Unicode.GetBytes(value.AsSpan(0, Math.Min(value.Length, 2047)), data.AsSpan(offset, 4096));
    }

    internal static void Check(int status, string operation, bool attempted = false)
    {
        if (status != 0)
        {
            var refused = status is -3 or -5 or -9 or -104 or -137 or -160 or -166 or -175;
            throw new DriverFailure($"NVAPI {operation} returned {status}.", attempted && !refused,
                status is -4 or -6 or -8 or -10);
        }
    }
}

internal sealed record NvOutput(uint Id, nint Gpu, DisplayTargetIdentity? Target = null);
