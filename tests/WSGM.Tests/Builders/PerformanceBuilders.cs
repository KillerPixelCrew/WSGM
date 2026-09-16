using WSGM.Core;

namespace WSGM.Tests.Builders;

/// <summary>Performance services over the hardware-free RTSS simulation.</summary>
internal static class PerformanceBuilders
{
    internal static PerformanceService Service(PerformancePolicy? policy = null)
    {
        return new PerformanceService(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            policy ?? PerformancePolicy.Empty);
    }
}
