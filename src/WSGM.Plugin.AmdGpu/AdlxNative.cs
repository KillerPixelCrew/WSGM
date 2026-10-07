// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.AmdGpu;

/// <summary>Exact documented C vtables. IADLXSystem and IADLMapping have no reference-count prefix.</summary>
internal static unsafe class AdlxNative
{
    internal const ulong HeaderVersion = (2UL << 48) | 125;
    private static long _epoch;
    internal static long Epoch => Interlocked.Read(ref _epoch);

    internal static nint Function(nint instance, int slot)
    {
        if (instance == 0)
        {
            throw new DriverFailure("The ADLX interface is unavailable.");
        }

        return (*(nint**)instance)[slot];
    }

    internal static AdlxObject Interface(nint instance, int slot)
    {
        nint value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Function(instance, slot))(instance, &value),
            "GetInterface");
        return new AdlxObject(value);
    }

    internal static AdlxObject Feature(nint service, int slot, nint target)
    {
        nint value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Function(service, slot))(service, target, &value),
            "GetFeature");
        return new AdlxObject(value);
    }

    // RSR and AFMF are service-wide interfaces. Their second argument is the output address,
    // so passing a GPU there would overwrite the beginning of the GPU object.
    internal static AdlxObject GraphicsFeature(nint service, int slot, nint gpu)
    {
        return slot is 14 or 17 ? Interface(service, slot) : Feature(service, slot, gpu);
    }

    internal static AdlxObject? Query(nint instance, string iid)
    {
        nint value = 0;
        int status;
        fixed (char* name = iid)
        {
            status = ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Function(instance, 2))(instance, name,
                &value);
        }

        if (status is 6 or 12)
        {
            return null;
        }

        Check(status, "QueryInterface");
        return new AdlxObject(value);
    }

    internal static List<AdlxObject> Items(nint list)
    {
        var count = ((delegate* unmanaged[Stdcall]<nint, uint>)Function(list, 3))(list);
        var result = new List<AdlxObject>();
        try
        {
            var begin = ((delegate* unmanaged[Stdcall]<nint, uint>)Function(list, 5))(list);
            for (uint offset = 0; offset < count; offset++)
            {
                nint value = 0;
                Check(
                    ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Function(list, 11))(list, begin + offset,
                        &value), "List.At");
                result.Add(new AdlxObject(value));
            }

            return result;
        }
        catch
        {
            foreach (var value in result)
            {
                value.Dispose();
            }

            throw;
        }
    }

    internal static string Text(nint instance, int slot)
    {
        nint text = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Function(instance, slot))(instance, &text), "GetString");
        return Marshal.PtrToStringUTF8(text) ?? "";
    }

    internal static int Integer(nint instance, int slot)
    {
        var value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Function(instance, slot))(instance, &value),
            "GetInteger");
        return value;
    }

    internal static nuint Size(nint instance, int slot)
    {
        nuint value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nuint*, int>)Function(instance, slot))(instance, &value), "GetSize");
        return value;
    }

    internal static bool Boolean(nint instance, int slot)
    {
        byte value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Function(instance, slot))(instance, &value),
            "GetBoolean");
        return value != 0;
    }

    internal static bool SupportsValue(nint instance, int slot, int candidate)
    {
        byte value = 0;
        Check(
            ((delegate* unmanaged[Stdcall]<nint, int, byte*, int>)Function(instance, slot))(instance, candidate,
                &value), "IsSupportedValue");
        return value != 0;
    }

    internal static AdlxRange Range(nint instance, int slot)
    {
        var value = new AdlxRange();
        Check(((delegate* unmanaged[Stdcall]<nint, AdlxRange*, int>)Function(instance, slot))(instance, &value),
            "GetRange");
        if (value.Minimum > value.Maximum || value.Step <= 0)
        {
            throw new DriverFailure("ADLX returned an invalid value range.");
        }

        return value;
    }

    internal static void SetInteger(nint instance, int slot, int value, WriteAdmission admission)
    {
        var function = (delegate* unmanaged[Stdcall]<nint, int, int>)Function(instance, slot);
        admission.Check();
        Check(function(instance, value), "SetInteger", true);
    }

    internal static void SetBoolean(nint instance, int slot, bool value, WriteAdmission admission)
    {
        var function = (delegate* unmanaged[Stdcall]<nint, byte, int>)Function(instance, slot);
        admission.Check();
        Check(function(instance, value ? (byte)1 : (byte)0), "SetBoolean", true);
    }

    internal static void Call(nint instance, int slot, WriteAdmission admission)
    {
        var function = (delegate* unmanaged[Stdcall]<nint, int>)Function(instance, slot);
        admission.Check();
        Check(function(instance), "Apply", true);
    }

    internal static void Check(int status, string operation, bool attempted = false)
    {
        if (status is not (0 or 1 or 2))
        {
            if (status is 7 or 11)
            {
                // AMD documents all outstanding interfaces as invalid after termination/orphaning.
                Interlocked.Increment(ref _epoch);
            }

            throw new DriverFailure($"ADLX {operation} returned {status}.",
                attempted && status is not (4 or 6 or 12 or 14 or 17),
                status is 7 or 11);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AdlxRange
{
    internal int Minimum;
    internal int Maximum;
    internal int Step;
}

internal sealed unsafe class AdlxObject : IDisposable
{
    private readonly long _epoch = AdlxNative.Epoch;
    private nint _pointer;

    internal AdlxObject(nint pointer)
    {
        _pointer = pointer != 0 ? pointer : throw new DriverFailure("ADLX returned an empty interface.");
    }

    internal nint Pointer => _epoch == AdlxNative.Epoch ? _pointer : 0;

    public void Dispose()
    {
        var pointer = Pointer;
        _pointer = 0;
        if (pointer == 0)
        {
            return;
        }

        ((delegate* unmanaged[Stdcall]<nint, int>)AdlxNative.Function(pointer, 1))(pointer);
    }
}
