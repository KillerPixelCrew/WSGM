using System.Threading.Tasks;
using LibGPUDriverInteract;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>The Intel driver calls the extended VRR range makes, in order and off the caller's thread.</summary>
/// <remarks>
///     Each call waits a bounded time for the driver. A null answer means it did not answer: a write is then
///     uncertain and never sent again automatically.
/// </remarks>
internal interface IExtendedVrrDriver
{
    /// <summary>Reads the display's Arc Sync range and profile.</summary>
    Task<GpuArcSyncState?> ReadArcSyncAsync(DisplayTargetIdentity target);

    /// <summary>Supplies the profile a released fixed refresh rate returns to, or null for the captured one.</summary>
    Task<bool> SetBaselineAsync(DisplayTargetIdentity target, GpuArcSyncBaseline? baseline);

    /// <summary>Writes the Arc Sync profile for the policy that owns it.</summary>
    Task<GpuDisplayWriteResult?> SetProfileAsync(DisplayTargetIdentity target, GpuArcSyncBaseline profile);

    /// <summary>Reads Intel's frame rate solutions for the display's adapter.</summary>
    Task<GpuVrrSolutions?> ReadSolutionsAsync(DisplayTargetIdentity target);

    /// <summary>Turns one of Intel's frame rate solutions on or off.</summary>
    Task<GpuDisplayWriteResult?> SetSolutionAsync(DisplayTargetIdentity target, GpuVrrSolution solution,
        bool enabled);
}
