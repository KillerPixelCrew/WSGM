// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;

namespace WSGM.Device.Asus.RogAllyX;

internal sealed record AsusReading(AsusReadControl Control, AsusScalar? Scalar, string? UnavailableReason);

/// <summary>Reads the scalar status controls once each. Not called by the plugin yet.</summary>
internal static class AsusReadings
{
    private static readonly AsusReadControl[] ScalarControls =
    [
        AsusReadControl.FirmwareProfile,
        AsusReadControl.SustainedPower,
        AsusReadControl.SlowPower,
        AsusReadControl.FastPower,
        AsusReadControl.ChargeLimit,
        AsusReadControl.CpuFanSpeed,
        AsusReadControl.GpuFanSpeed
    ];

    internal static IReadOnlyList<AsusReading> ReadScalars(IAsusReader reader, CancellationToken cancellationToken)
    {
        List<AsusReading> results = [];
        foreach (var control in ScalarControls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = reader.Read(control, 0, cancellationToken);
                results.Add(AsusReadProtocol.TryReadScalar(response, out var scalar)
                    ? new AsusReading(control, scalar, null)
                    : new AsusReading(control, null, "Unsupported or malformed DSTS response."));
            }
            catch (Exception e) when (e is IOException or Win32Exception)
            {
                results.Add(new AsusReading(control, null, e.Message));
            }
        }

        return results.AsReadOnly();
    }
}
