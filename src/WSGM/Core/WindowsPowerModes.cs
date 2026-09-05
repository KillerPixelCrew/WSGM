using System;
using System.Threading;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Interop;

namespace WSGM.Core;

internal sealed class WindowsPowerModes(IPowerModeApi api)
{
    internal static WindowsPowerModes Windows { get; } = new(new WindowsPowerModeApi());

    internal static Guid Id(DevicePowerMode mode) => mode switch
    {
        DevicePowerMode.BetterBattery => new("961cc777-2547-4f9d-8174-7d86181b8a7a"),
        DevicePowerMode.Balanced => Guid.Empty,
        DevicePowerMode.BestPerformance => new("ded574b5-45a0-4f42-8737-46345c09c238"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal Guid Read() => api.Read();

    internal static string Label(DevicePowerMode mode) => mode switch
    {
        DevicePowerMode.BetterBattery => "Better Battery",
        DevicePowerMode.Balanced => "Balanced",
        DevicePowerMode.BestPerformance => "Best Performance",
        _ => "Unknown",
    };

    internal void Apply(DevicePowerMode mode, CancellationToken cancellationToken)
    {
        lock (PowerSchemes.MutationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid id = Id(mode);
            api.Set(id);
            if (api.Read() != id)
            {
                throw new InvalidOperationException("Windows did not confirm the requested power mode.");
            }
        }
    }
}
