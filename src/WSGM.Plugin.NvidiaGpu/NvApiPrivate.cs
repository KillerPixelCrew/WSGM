// SPDX-License-Identifier: MIT
// ColorControl d0d3bb4: private NVIDIA dithering and HDR output-mode ABI.
// These entry points are optional. They never trigger a registry write or driver restart.

using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.NvidiaGpu;

internal sealed unsafe partial class NvApi
{
    internal uint OutputMode(uint display)
    {
        uint mode = 0;
        Check(((delegate* unmanaged[Cdecl]<uint, uint*, int>)Function(0x81fed88d))(display, &mode),
            "Disp_GetOutputMode");
        return mode;
    }

    internal void SetOutputMode(uint display, uint mode, WriteAdmission admission)
    {
        var function = (delegate* unmanaged[Cdecl]<uint, uint*, int>)Function(0x98e7661a);
        admission.Check();
        Check(function(display, &mode), "Disp_SetOutputMode", true);
    }

    internal NvDither Dither(uint display)
    {
        var data = new byte[24];
        Number(data, 0, Version(data.Length, 1));
        fixed (byte* pointer = data)
        {
            Check(((delegate* unmanaged[Cdecl]<uint, byte*, int>)Function(0x932ac8fb))(display, pointer),
                "Disp_GetDitherControl");
        }

        return new NvDither(Number(data, 4), Number(data, 8), Number(data, 12), Number(data, 16), Number(data, 20));
    }

    internal void SetDither(NvOutput expected, NvDither data, WriteAdmission admission, bool preserveState = false)
    {
        var output = RequireOutput(expected);
        var function = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, int>)Function(0xdf0dfcdd);
        admission.Check();
        Check(function(output.Gpu, output.Id, data.State, data.State == 0 && !preserveState ? 0 : data.Bits,
            data.State == 0 && !preserveState ? 0 : data.Mode), "Disp_SetDitherControl", true);
    }
}

internal sealed record NvDither(uint State, uint Bits, uint Mode, uint BitsCaps, uint ModeCaps);
