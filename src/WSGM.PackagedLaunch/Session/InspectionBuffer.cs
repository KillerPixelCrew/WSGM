using System;
using System.Runtime.InteropServices;

namespace WSGM.PackagedLaunch;

/// <summary>Owns the one native buffer used by a sized inspection query.</summary>
public sealed class InspectionBuffer : IDisposable
{
    private readonly Func<int, nint> _allocate;
    private readonly Action<nint> _free;

    /// <summary>Creates a buffer owner, optionally using a caller's allocation operations.</summary>
    /// <param name="allocate">Allocates a buffer, or null to use Marshal.</param>
    /// <param name="free">Releases an owned buffer, or null to use Marshal.</param>
    public InspectionBuffer(Func<int, nint>? allocate = null, Action<nint>? free = null)
    {
        _allocate = allocate ?? Marshal.AllocHGlobal;
        _free = free ?? Marshal.FreeHGlobal;
    }

    /// <summary>The currently owned address, or zero before allocation or after release.</summary>
    public nint Pointer { get; private set; }

    /// <summary>Releases the owned address once.</summary>
    public void Dispose()
    {
        var pointer = Pointer;
        Pointer = 0;
        if (pointer != 0)
        {
            _free(pointer);
        }
    }

    /// <summary>Replaces the owned buffer, leaving no old ownership if allocation fails.</summary>
    /// <param name="bytes">The API's requested byte size.</param>
    public void Resize(int bytes)
    {
        Dispose();
        Pointer = _allocate(bytes);
    }
}

/// <summary>Runs a containment assignment only after its creation identity is established.</summary>
public static class ProcessContainmentIdentity
{
    /// <summary>Validates a creation time before invoking the assignment.</summary>
    /// <param name="expected">The discovery's creation time, or null when unknown.</param>
    /// <param name="opened">Reads creation time from the opened process handle.</param>
    /// <param name="assign">Assigns that same handle once.</param>
    /// <returns>Whether identity matched and assignment was accepted.</returns>
    public static bool TryAssign(DateTime? expected, Func<DateTime?> opened, Func<bool> assign)
    {
        return expected is { } known && opened() is { } current && current == known && assign();
    }
}
