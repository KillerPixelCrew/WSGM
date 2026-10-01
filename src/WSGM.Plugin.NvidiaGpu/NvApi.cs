using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.NvidiaGpu;

/// <summary>Only documented NVAPI entry points and the pinned DWORD DRS/color layouts.</summary>
internal sealed unsafe class NvApi : IDisposable
{
    internal const int SettingSize = 12320;
    internal const int SettingIdOffset = 4100;
    internal const int CurrentValueOffset = 8220;
    internal const int ValuesSize = 414112;
    internal const int ValueStride = 4100;
    internal const int ApplicationSize = 20492;
    internal const int ProfileSize = 4116;
    internal const int ColorSize = 24;
    private nint _library;
    private readonly delegate* unmanaged[Cdecl]<uint, nint> _query;
    internal nint Drs { get; private set; }

    internal NvApi()
    {
        _library = NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll"));
        _query = (delegate* unmanaged[Cdecl]<uint, nint>)NativeLibrary.GetExport(_library, "nvapi_QueryInterface");
        try
        {
            Check(((delegate* unmanaged[Cdecl]<int>)Function(0x0150e828))(), "Initialize");
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

    internal nint Function(uint id) => _query(id) is var pointer && pointer != 0
        ? pointer : throw new DriverFailure($"NVAPI function 0x{id:X8} is unavailable.");

    internal void Load() => Check(((delegate* unmanaged[Cdecl]<nint, int>)Function(0x375dbd6b))(Drs), "DRS LoadSettings");
    internal void Save() => Check(((delegate* unmanaged[Cdecl]<nint, int>)Function(0xfcbc7e14))(Drs), "DRS SaveSettings", attempted: true);

    internal nint GlobalProfile()
    {
        nint profile = 0;
        Check(((delegate* unmanaged[Cdecl]<nint, nint*, int>)Function(0x617bff9f))(Drs, &profile), "DRS GetCurrentGlobalProfile");
        return profile;
    }

    internal (uint Value, bool Explicit) Get(nint profile, uint setting)
    {
        var buffer = NewSetting(setting);
        fixed (byte* pointer = buffer)
        {
            Check(((delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, int>)Function(0x73bf8338))(Drs, profile, setting, pointer), "DRS GetSetting");
        }
        if (Number(buffer, 4104) != 0)
        {
            throw new DriverFailure("The setting is not a documented DWORD setting.");
        }
        return (Number(buffer, CurrentValueOffset), Number(buffer, 4108) == 0 && Number(buffer, 4112) == 0);
    }

    internal void Set(nint profile, uint setting, uint value)
    {
        var buffer = NewSetting(setting);
        Number(buffer, CurrentValueOffset, value);
        fixed (byte* pointer = buffer)
        {
            Check(((delegate* unmanaged[Cdecl]<nint, nint, byte*, int>)Function(0x577dd202))(Drs, profile, pointer), "DRS SetSetting", attempted: true);
        }
    }

    internal void Inherit(nint profile, uint setting)
    {
        Check(((delegate* unmanaged[Cdecl]<nint, nint, uint, int>)Function(0x53f0381e))(Drs, profile, setting),
            "DRS RestoreProfileDefaultSetting", attempted: true);
    }

    internal uint[] Values(uint setting)
    {
        var buffer = new byte[ValuesSize];
        Number(buffer, 0, Version(ValuesSize, 1));
        uint count = 100;
        fixed (byte* pointer = buffer)
        {
            Check(((delegate* unmanaged[Cdecl]<uint, uint*, byte*, int>)Function(0x2ec39f90))(setting, &count, pointer), "DRS EnumAvailableSettingValues");
        }
        if (Number(buffer, 8) != 0 || count > 100 || Number(buffer, 4) > 100)
        {
            throw new DriverFailure("The setting value table is unsupported.");
        }
        return Enumerable.Range(0, (int)Math.Min(count, Number(buffer, 4)))
            .Select(index => Number(buffer, 12 + ValueStride + index * ValueStride)).Distinct().ToArray();
    }

    internal nint Application(string executable, bool create, string profileName)
    {
        if (executable.Length is 0 or > 260 || Path.GetFileName(executable) != executable || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
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
            status = ((delegate* unmanaged[Cdecl]<nint, char*, nint*, byte*, int>)Function(0xeee566b2))(Drs, name, &profile, pointer);
        }
        if (status == 0)
        {
            return profile;
        }
        // NVAPI_EXECUTABLE_NOT_FOUND is the sole permission to create an application association.
        if (status != -166 || !create)
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
                Check(((delegate* unmanaged[Cdecl]<nint, byte*, nint*, int>)Function(0xcc176068))(Drs, pointer, &profile), "DRS CreateProfile", attempted: true);
            }
        }
        else
        {
            Check(status, "DRS FindProfileByName");
        }
        fixed (byte* pointer = app)
        {
            Check(((delegate* unmanaged[Cdecl]<nint, nint, byte*, int>)Function(0x4347a9de))(Drs, profile, pointer), "DRS CreateApplication", attempted: true);
        }
        return profile;
    }

    internal uint[] Displays()
    {
        var gpus = new nint[64];
        uint count = 0;
        fixed (nint* pointer = gpus)
        {
            Check(((delegate* unmanaged[Cdecl]<nint*, uint*, int>)Function(0xe5ac921f))(pointer, &count), "EnumPhysicalGPUs");
        }
        if (count is 0 or > 64)
        {
            throw new DriverFailure("No NVIDIA physical GPU is available.");
        }
        var displays = new HashSet<uint>();
        foreach (var gpu in gpus.Take((int)count))
        {
            uint outputs = 0;
            var get = (delegate* unmanaged[Cdecl]<nint, byte*, uint*, uint, int>)Function(0x0078dba2);
            Check(get(gpu, null, &outputs, 0), "GetConnectedDisplayIds count");
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
                Check(get(gpu, pointer, &outputs, 0), "GetConnectedDisplayIds");
            }
            if (outputs > data.Length / 16)
            {
                throw new DriverFailure("The NVIDIA display topology changed during enumeration.");
            }
            for (var index = 0; index < outputs; index++)
            {
                if ((Number(data, index * 16 + 12) & 4) != 0)
                {
                    displays.Add(Number(data, index * 16 + 8));
                }
            }
        }
        return displays.Order().ToArray();
    }

    internal byte[] Color(uint display, byte command, byte[]? input = null)
    {
        var buffer = input is null ? new byte[ColorSize] : (byte[])input.Clone();
        Number(buffer, 0, Version(ColorSize, 5));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), ColorSize);
        buffer[6] = command;
        fixed (byte* pointer = buffer)
        {
            Check(((delegate* unmanaged[Cdecl]<uint, byte*, int>)Function(0x92f9d80d))(display, pointer), "Disp_ColorControl", attempted: command == 2);
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_library == 0)
        {
            return;
        }
        if (Drs != 0)
        {
            ((delegate* unmanaged[Cdecl]<nint, int>)Function(0xdad9cff8))(Drs);
            Drs = 0;
        }
        ((delegate* unmanaged[Cdecl]<int>)Function(0xd22bdd7e))();
        NativeLibrary.Free(_library);
        _library = 0;
    }

    internal static uint Version(int size, int revision) => (uint)size | (uint)revision << 16;
    internal static uint Number(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    internal static void Number(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static byte[] NewSetting(uint setting)
    {
        var data = new byte[SettingSize];
        Number(data, 0, Version(SettingSize, 1));
        Number(data, SettingIdOffset, setting);
        return data;
    }
    private static void Unicode(byte[] data, int offset, string value) => Encoding.Unicode.GetBytes(value.AsSpan(0, Math.Min(value.Length, 2047)), data.AsSpan(offset, 4096));
    internal static void Check(int status, string operation, bool attempted = false)
    {
        if (status != 0)
        {
            throw new DriverFailure($"NVAPI {operation} returned {status}.", attempted);
        }
    }
}
