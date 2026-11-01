using System;
using System.Threading;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>The Windows power-mode overlay (Better Battery, Balanced, Best Performance).</summary>
/// <remarks>Writes take the scheme owner's mutation lock with every other machine-wide power change.</remarks>
/// <param name="schemes">Session owner supplying the shared machine-policy mutation lock.</param>
/// <param name="api">Platform overlay API; native failures propagate to the caller.</param>
internal sealed class WindowsPowerModes(PowerSchemes schemes, IPowerModeApi api)
{
    /// <summary>Maps a supported device power mode to its Windows overlay GUID.</summary>
    /// <param name="mode">Battery, balanced, or performance mode.</param>
    /// <returns>The overlay GUID; Balanced maps to the empty GUID.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not supported.</exception>
    internal static Guid Id(DevicePowerMode mode)
    {
        return mode switch
        {
            DevicePowerMode.BetterBattery => new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"),
            DevicePowerMode.Balanced => Guid.Empty,
            DevicePowerMode.BestPerformance => new Guid("ded574b5-45a0-4f42-8737-46345c09c238"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>Reads the effective Windows power-mode overlay.</summary>
    /// <returns>The current native overlay GUID, which may be outside the offered catalog.</returns>
    internal Guid Read()
    {
        return api.Read();
    }

    /// <summary>Formats a device power mode for the UI.</summary>
    /// <param name="mode">Mode to describe.</param>
    /// <returns>The fixed display label, or Unknown for an unrecognized value.</returns>
    internal static string Label(DevicePowerMode mode)
    {
        return mode switch
        {
            DevicePowerMode.BetterBattery => "Better Battery",
            DevicePowerMode.Balanced => "Balanced",
            DevicePowerMode.BestPerformance => "Best Performance",
            _ => "Unknown"
        };
    }

    /// <summary>Writes the selected overlay once while holding the shared policy lock.</summary>
    /// <param name="mode">Supported mode to apply.</param>
    /// <param name="cancellationToken">Checked under the lock before native dispatch.</param>
    /// <remarks>No confirming read or rollback follows acceptance. Invalid modes and native failures propagate.</remarks>
    /// <exception cref="OperationCanceledException">Cancellation was requested before dispatch.</exception>
    internal void Apply(DevicePowerMode mode, CancellationToken cancellationToken)
    {
        using (schemes.EnterMutation())
        {
            cancellationToken.ThrowIfCancellationRequested();
            api.Set(Id(mode));
        }
    }
}
