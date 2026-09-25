using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Worker;

/// <summary>
///     Every hardware service the worker hosts. A service not listed here cannot be opened, and only the
///     methods of its interface can be called.
/// </summary>
internal static class LabWorkerServices
{
    /// <summary>The services by name.</summary>
    public static IReadOnlyDictionary<string, LabWorkerService> All { get; } =
        new LabWorkerService[]
        {
            Transports.LabAtkAcpi.Service,
            Transports.LabMsiWmi.Service,
            Transports.LabAuraLighting.Service,
            Transports.LabAmdSmu.Service,
            Transports.LabIntelKx.Service
        }.ToDictionary(service => service.Name);
}
