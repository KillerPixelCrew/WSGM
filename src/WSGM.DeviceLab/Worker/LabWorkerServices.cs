using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Transports;

namespace WSGM.DeviceLab.Worker;

/// <summary>
///     Every hardware service the worker hosts. A service not listed here cannot be opened, and only the
///     methods of its interface can be called.
/// </summary>
internal static class LabWorkerServices
{
    /// <summary>The services by name.</summary>
    public static IReadOnlyDictionary<string, LabWorkerService> All { get; } =
        new[]
        {
            LabAtkAcpi.Service,
            LabMsiWmi.Service,
            LabAuraLighting.Service,
            LabAmdSmu.Service,
            LabIntelKx.Service,
            LabRumbleWorker.Service,
            LabCuratedInitWorker.Service
        }.ToDictionary(service => service.Name);
}
