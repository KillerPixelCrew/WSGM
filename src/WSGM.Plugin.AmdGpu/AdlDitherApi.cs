// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.AmdGpu;

/// <summary>The documented ADL2 dithering calls, mapped from the ADLX display instead of a fixed index.</summary>
internal sealed unsafe class AdlDitherApi : IDisposable
{
    private readonly delegate* unmanaged[Cdecl]<nint, int, int, int*, int> _get;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int, int, int> _set;
    private nint _context;
    private nint _library;

    internal AdlDitherApi()
    {
        _library = NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "atiadlxx.dll"));
        try
        {
            _get = (delegate* unmanaged[Cdecl]<nint, int, int, int*, int>)NativeLibrary.GetExport(_library,
                "ADL2_Display_DitherState_Get");
            _set = (delegate* unmanaged[Cdecl]<nint, int, int, int, int>)NativeLibrary.GetExport(_library,
                "ADL2_Display_DitherState_Set");
            nint context = 0;
            Check(((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, nint>, int, nint*, int>)
                    NativeLibrary.GetExport(_library, "ADL2_Main_Control_Create"))(&Allocate, 1, &context),
                "Main_Control_Create");
            _context = context != 0 ? context : throw new DriverFailure("ADL2 returned no context.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_library == 0)
        {
            return;
        }

        try
        {
            if (_context != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "ADL2_Main_Control_Destroy"))(
                    _context);
                _context = 0;
            }
        }
        finally
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    internal int Read(int adapter, int display)
    {
        var state = 0;
        Check(_get(_context, adapter, display, &state), "Display_DitherState_Get");
        return state;
    }

    internal void Write(int adapter, int display, int state)
    {
        DriverWriteScope.Check();
        Check(_set(_context, adapter, display, state), "Display_DitherState_Set", true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Allocate(int size)
    {
        try
        {
            return size is > 0 and <= 64 * 1024 * 1024 ? Marshal.AllocCoTaskMem(size) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void Check(int status, string operation, bool attempted = false)
    {
        if (status != 0)
        {
            throw new DriverFailure($"ADL2 {operation} returned {status}.", attempted && status is not (-3 or -8));
        }
    }
}
