using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Msi.Claw8A2Vm;

internal sealed class ClawA2VmPowerCapability(IMsiWmiTransport transport)
{
    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<PowerPair> ReadAsync(CancellationToken cancellationToken)
    {
        var sustained = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.PowerSustainedAddress,
            cancellationToken).ConfigureAwait(false);
        var boost = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.PowerBoostAddress,
            cancellationToken).ConfigureAwait(false);
        var scenario = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.ScenarioAddress,
            cancellationToken).ConfigureAwait(false);
        return scenario.Length < 2
            ? throw new InvalidOperationException("The scenario getter returned a truncated response.")
            : new PowerPair(ReadInt32(sustained), ReadInt32(boost), scenario[1]);
    }

    public async ValueTask<CapabilityCommandResult> ApplySustainedAsync(
        CapabilityCommand command,
        int watts,
        CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (command.ApplyPowerPair)
        {
            if (watts is < 8 or > 37)
            {
                return ClawResults.Rejected(
                    command,
                    CapabilityReasonCode.ValueOutOfRange,
                    "The power pair must be 8-37 W.");
            }
            return await ApplyPairCoreAsync(command, before, watts, watts, cancellationToken).ConfigureAwait(false);
        }

        // PL1's ceiling is the same 37 W as PL2, raised from 30 W on the maintainer's instruction
        // for the A2VM. `_plan/claw-8-a2vm-plugin.md` recorded 8-30 W for EC 0x50 from the stock
        // read, which is the value the firmware ships with rather than the range it accepts.
        // ApplyPairCoreAsync reads the pair back and only reports success when the hardware took
        // the value, so a ceiling the EC actually refuses surfaces as a failed command rather than
        // a silent lie.
        if (watts is < 8 or > 37 || before.BoostWatts < watts || before.BoostWatts > 37)
        {
            return ClawResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "PL1 must be 8-37 W and cannot exceed the current PL2 value.");
        }

        return await ApplyPairCoreAsync(command, before, watts, before.BoostWatts, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CapabilityCommandResult> ApplyBoostAsync(
        CapabilityCommand command,
        int watts,
        CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (watts is < 8 or > 37 || watts < before.SustainedWatts)
        {
            return ClawResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "PL2 must be 8-37 W and cannot be below the current PL1 value.");
        }

        return await ApplyPairCoreAsync(command, before, before.SustainedWatts, watts, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CapabilityCommandResult> ApplyScenarioAsync(
        CapabilityCommand command,
        string scenario,
        CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if ((before.Scenario & 0x80) == 0)
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                "Firmware does not support scenario selection.");
        }
        var target = scenario switch
        {
            "comfort" => 0xC0,
            "green" => 0xC1,
            "eco" => 0xC2,
            "user" => 0xC3,
            "sport" => 0xC4,
            "inactive" => before.Scenario & ~0x40,
            _ => -1
        };
        if (target < 0)
        {
            return ClawResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, "Unknown firmware scenario.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (before.Scenario != target)
            {
                await WriteDataAsync(ClawHardwareFacts.ScenarioAddress, target, cancellationToken).ConfigureAwait(false);
            }
            var readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (readback.Scenario == target)
            {
                return ClawResults.Verified(command, CapabilityValue.Choice(scenario));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The firmware may have changed both scenario and watt limits before reporting failure.
        }
        RollbackResult rollback;
        try
        {
            rollback = await RestoreAsync(before, CancellationToken.None).ConfigureAwait(false)
                ? RollbackResult.RestoredVerified : RollbackResult.RestoredUnverified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            rollback = RollbackResult.RestoreFailed;
        }
        return ClawResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            "Firmware scenario readback failed.",
            rollback);
    }

    public async ValueTask<bool> RestoreAsync(PowerPair snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Scenario != snapshot.Scenario)
        {
            await WriteDataAsync(ClawHardwareFacts.ScenarioAddress, snapshot.Scenario, cancellationToken)
                .ConfigureAwait(false);
            current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        // A scenario switch can reset watt limits. Restore the exact pair after the scenario.
        await WritePairOrderedAsync(current, snapshot.SustainedWatts, snapshot.BoostWatts, cancellationToken)
            .ConfigureAwait(false);

        var readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return readback == snapshot;
    }

    private async ValueTask<CapabilityCommandResult> ApplyPairCoreAsync(
        CapabilityCommand command,
        PowerPair before,
        int sustainedWatts,
        int boostWatts,
        CancellationToken cancellationToken)
    {
        try
        {
            await WritePairOrderedAsync(before, sustainedWatts, boostWatts, cancellationToken)
                .ConfigureAwait(false);
            var readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (readback.SustainedWatts == sustainedWatts && readback.BoostWatts == boostWatts)
            {
                var value = command.CapabilityId == CapabilityIds.PowerSustained
                    ? readback.SustainedWatts
                    : readback.BoostWatts;
                return ClawResults.Verified(command, CapabilityValue.Integer(value));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // At least one write may have reached firmware. Fall through to the exact captured-pair
            // rollback and report the independently verified restoration result.
        }

        var rollback = await TryRestorePairAsync(before, CancellationToken.None).ConfigureAwait(false);
        return ClawResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            "Power readback did not match the requested pair.",
            rollback);
    }

    private async ValueTask WritePairOrderedAsync(
        PowerPair current,
        int sustainedWatts,
        int boostWatts,
        CancellationToken cancellationToken)
    {
        if (sustainedWatts > current.BoostWatts)
        {
            await WriteDataAsync(ClawHardwareFacts.PowerBoostAddress, boostWatts, cancellationToken)
                .ConfigureAwait(false);
            await WriteDataAsync(ClawHardwareFacts.PowerSustainedAddress, sustainedWatts, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await WriteDataAsync(ClawHardwareFacts.PowerSustainedAddress, sustainedWatts, cancellationToken)
                .ConfigureAwait(false);
            if (boostWatts != current.BoostWatts)
            {
                await WriteDataAsync(ClawHardwareFacts.PowerBoostAddress, boostWatts, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<RollbackResult> TryRestorePairAsync(
        PowerPair snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            await WritePairOrderedAsync(current, snapshot.SustainedWatts, snapshot.BoostWatts, cancellationToken)
                .ConfigureAwait(false);
            var readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return readback.SustainedWatts == snapshot.SustainedWatts
                && readback.BoostWatts == snapshot.BoostWatts
                ? RollbackResult.RestoredVerified
                : RollbackResult.RestoredUnverified;
        }
        catch
        {
            return RollbackResult.RestoreFailed;
        }
    }

    private ValueTask WriteDataAsync(byte address, int value, CancellationToken cancellationToken)
    {
        var package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = address;
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(1, sizeof(int)), value);
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }

    private static int ReadInt32(byte[] response)
    {
        return response.Length < 1 + sizeof(int)
            ? throw new InvalidOperationException("The power getter returned a truncated response.")
            : BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(1, sizeof(int)));
    }
}

internal sealed class ClawA2VmChargeLimitCapability(IMsiWmiTransport transport)
{
    internal const int MinimumPercent = 60;
    internal const int MaximumPercent = 100;

    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<ChargeLimitState> ReadAsync(CancellationToken cancellationToken)
    {
        var response = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.ChargeLimitAddress,
            cancellationToken).ConfigureAwait(false);
        if (response.Length < 2)
        {
            throw new InvalidOperationException("The charge-limit response was truncated.");
        }

        var rawValue = response[1];
        if (rawValue is < MinimumPercent or > MaximumPercent)
        {
            throw new InvalidOperationException(
                $"The charge-limit value {rawValue}% is outside the supported range.");
        }

        return new ChargeLimitState(rawValue, rawValue);
    }

    public async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        int percent,
        CancellationToken cancellationToken)
    {
        if (percent is < MinimumPercent or > MaximumPercent)
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.ValueOutOfRange,
                $"The charge limit must be {MinimumPercent}-{MaximumPercent}%.");
        }

        var before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var wanted = Encode(percent);
        try
        {
            await WriteRawAsync(wanted, cancellationToken).ConfigureAwait(false);
            var readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (readback.Percent == percent)
            {
                return ClawResults.Verified(command, CapabilityValue.Integer(readback.Percent));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Set_Data does not report whether firmware accepted a timed-out write. The exact raw
            // byte captured immediately beforehand is the only safe rollback value.
            PluginTrace.Failure("charge-limit", "Charge-limit write or readback failed", exception);
        }

        var rollback = await TryRestoreAsync(before.RawValue, CancellationToken.None)
            .ConfigureAwait(false);
        return ClawResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            "Charge-limit readback did not match the requested policy.",
            rollback);
    }

    private static byte Encode(int percent) => checked((byte)percent);

    private async ValueTask<RollbackResult> TryRestoreAsync(
        byte rawValue,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteRawAsync(rawValue, cancellationToken).ConfigureAwait(false);
            var response = await _transport.InvokeGetterAsync(
                "Get_Data",
                ClawHardwareFacts.ChargeLimitAddress,
                cancellationToken).ConfigureAwait(false);
            return response.Length >= 2 && response[1] == rawValue
                ? RollbackResult.RestoredVerified
                : RollbackResult.RestoredUnverified;
        }
        catch
        {
            return RollbackResult.RestoreFailed;
        }
    }

    private ValueTask WriteRawAsync(byte rawValue, CancellationToken cancellationToken)
    {
        var package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = ClawHardwareFacts.ChargeLimitAddress;
        package[1] = rawValue;
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }
}

internal sealed class ClawA2VmFanCapability(IMsiWmiTransport transport)
{
    private static readonly int[] TemperatureOffsets = [1, 4, 5, 6, 7, 8];
    private static readonly int[] DutyOffsets = [2, 3, 4, 5, 6, 7];

    /// <summary>The firmware's two fan channels, always written together.</summary>
    private static readonly int[] FanChannels = [1, 2];

    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<FanSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var left = await ReadTableAsync(1, cancellationToken).ConfigureAwait(false);
        var right = await ReadTableAsync(2, cancellationToken).ConfigureAwait(false);
        var custom = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.FanCustomAddress,
            cancellationToken).ConfigureAwait(false);
        var full = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.FanFullSpeedAddress,
            cancellationToken).ConfigureAwait(false);
        if (custom.Length < 2 || full.Length < 2)
        {
            throw new InvalidOperationException("The fan mode getter returned a truncated response.");
        }
        return new FanSnapshot(left, right, custom[1], full[1]);
    }

    public async ValueTask<FanTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken)
    {
        var fan = await _transport.InvokeGetterAsync("Get_Fan", 0, cancellationToken)
            .ConfigureAwait(false);
        var temperature = await _transport.InvokeGetterAsync("Get_Temperature", 0, cancellationToken)
            .ConfigureAwait(false);
        if (fan.Length < 5 || temperature.Length < 2)
        {
            throw new InvalidOperationException("The fan telemetry getter returned a truncated response.");
        }
        return new FanTelemetry(
            DecodeRpm(fan[1], fan[2]),
            DecodeRpm(fan[3], fan[4]),
            temperature[1]);
    }

    public async ValueTask<CapabilityCommandResult> ApplyModeAsync(
        CapabilityCommand command,
        string mode,
        CancellationToken cancellationToken)
    {
        if (mode is not ("automatic" or "custom" or "full-speed"))
        {
            return ClawResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, "Unknown fan mode.");
        }
        var (custom, full) = mode switch
        {
            "automatic" => (false, false),
            "custom" => (true, false),
            _ => (false, true)
        };
        var before = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteFlagAsync(
                ClawHardwareFacts.FanCustomAddress,
                before.CustomFlag,
                custom,
                cancellationToken).ConfigureAwait(false);
            await WriteFlagAsync(
                ClawHardwareFacts.FanFullSpeedAddress,
                before.FullSpeedFlag,
                full,
                cancellationToken).ConfigureAwait(false);
            var readback = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (Flag(readback.CustomFlag) == custom && Flag(readback.FullSpeedFlag) == full)
            {
                return ClawResults.Verified(command, CapabilityValue.Choice(mode));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The exact snapshot below is the recovery authority after any partial flag write.
        }

        var rollback = await TryRestoreAsync(before, CancellationToken.None).ConfigureAwait(false);
        return ClawResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            "Fan-mode readback did not match.",
            rollback);
    }

    /// <summary>Writes one curve to both fan channels as a single all-or-nothing change.</summary>
    /// <param name="command">The command being served, for the result it returns.</param>
    /// <param name="curve">The six validated points to install on both channels.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Verified only when both channels read the curve back.</returns>
    /// <remarks>
    /// The A2VM's two fans sit on one heatsink and the firmware ramps them together; a curve that
    /// applied to one of them would describe a machine that does not exist. Both channels are
    /// therefore written under ONE pre-write snapshot, so a failure on the second channel restores
    /// the first as well. Two separate <c>ApplyCurveAsync</c> calls could not: the second call's
    /// snapshot would already contain the first call's write and would happily "restore" to it,
    /// leaving the fans running curves that disagree.
    /// </remarks>
    public async ValueTask<CapabilityCommandResult> ApplyCurveAsync(
        CapabilityCommand command,
        IReadOnlyList<CurvePoint> curve,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCurve(curve, out var validationError))
        {
            return ClawResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, validationError!);
        }

        var before = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var applied = true;
            foreach (var channel in FanChannels)
            {
                var current = channel == 1 ? before.Left : before.Right;
                byte[] temperatures = [.. current.TemperatureBuffer];
                byte[] duties = [.. current.DutyBuffer];
                for (var i = 0; i < curve.Count; i++)
                {
                    temperatures[TemperatureOffsets[i]] = checked((byte)curve[i].Input);
                    duties[DutyOffsets[i]] = checked((byte)curve[i].Output);
                }

                await WriteTableAsync(channel, temperatures, duties, cancellationToken)
                    .ConfigureAwait(false);
                var readback = await ReadTableAsync(checked((byte)channel), cancellationToken)
                    .ConfigureAwait(false);
                applied &= CurveEquals(readback, curve);
            }

            if (applied)
            {
                return ClawResults.Verified(command, CapabilityValue.Curve([.. curve]));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The exact two-channel snapshot below restores every touched byte and flag.
        }

        var rollback = await TryRestoreAsync(before, CancellationToken.None).ConfigureAwait(false);
        return ClawResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            "Fan-table readback did not match.",
            rollback);
    }

    public async ValueTask<bool> RestoreAsync(FanSnapshot snapshot, CancellationToken cancellationToken)
    {
        return await TryRestoreAsync(snapshot, cancellationToken).ConfigureAwait(false)
            is RollbackResult.RestoredVerified;
    }

    private static bool TryValidateCurve(IReadOnlyList<CurvePoint> curve, out string? error)
    {
        if (curve.Count != 6)
        {
            error = "The A2VM firmware requires exactly six fan-curve points.";
            return false;
        }

        for (var i = 0; i < curve.Count; i++)
        {
            var point = curve[i];
            if (point.Input is < 0 or > 100 || point.Output is < 0 or > 100)
            {
                error = "Fan temperatures and duties must be in the validated 0-100 range.";
                return false;
            }

            if (i <= 0 || (point.Input >= curve[i - 1].Input && point.Output >= curve[i - 1].Output))
            {
                continue;
            }

            error = "Fan-curve temperatures and duties must be monotonic.";
            return false;
        }

        error = null;
        return true;
    }

    internal static IReadOnlyList<CurvePoint> DecodeCurve(FanTable table) =>
    [
        .. Enumerable.Range(0, TemperatureOffsets.Length)
            .Select(index => new CurvePoint(
                table.TemperatureBuffer[TemperatureOffsets[index]],
                table.DutyBuffer[DutyOffsets[index]]))
    ];

    private async ValueTask<FanTable> ReadTableAsync(byte channel, CancellationToken cancellationToken)
    {
        var duties = await _transport.InvokeGetterAsync("Get_Fan", channel, cancellationToken)
            .ConfigureAwait(false);
        var temperatures = await _transport.InvokeGetterAsync("Get_Temperature", channel, cancellationToken)
            .ConfigureAwait(false);
        return new FanTable(duties, temperatures);
    }

    private async ValueTask WriteTableAsync(
        int channel,
        byte[] temperatures,
        byte[] duties,
        CancellationToken cancellationToken)
    {
        byte[] temperaturePackage = [.. temperatures];
        byte[] dutyPackage = [.. duties];
        temperaturePackage[0] = checked((byte)channel);
        dutyPackage[0] = checked((byte)channel);
        await _transport.InvokeSetterAsync("Set_Temperature", temperaturePackage, cancellationToken)
            .ConfigureAwait(false);
        await _transport.InvokeSetterAsync("Set_Fan", dutyPackage, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<RollbackResult> TryRestoreAsync(
        FanSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteTableAsync(1, snapshot.Left.TemperatureBuffer, snapshot.Left.DutyBuffer, cancellationToken)
                .ConfigureAwait(false);
            await WriteTableAsync(2, snapshot.Right.TemperatureBuffer, snapshot.Right.DutyBuffer, cancellationToken)
                .ConfigureAwait(false);
            await WriteRawFlagAsync(
                ClawHardwareFacts.FanCustomAddress,
                snapshot.CustomFlag,
                cancellationToken).ConfigureAwait(false);
            await WriteRawFlagAsync(
                ClawHardwareFacts.FanFullSpeedAddress,
                snapshot.FullSpeedFlag,
                cancellationToken).ConfigureAwait(false);
            var readback = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return SnapshotEquals(snapshot, readback)
                ? RollbackResult.RestoredVerified
                : RollbackResult.RestoredUnverified;
        }
        catch
        {
            return RollbackResult.RestoreFailed;
        }
    }

    private ValueTask WriteFlagAsync(
        byte address,
        byte current,
        bool enabled,
        CancellationToken cancellationToken) =>
        WriteRawFlagAsync(address, enabled ? (byte)(current | 0x80) : (byte)(current & 0x7F), cancellationToken);

    private ValueTask WriteRawFlagAsync(byte address, byte value, CancellationToken cancellationToken)
    {
        var package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = address;
        package[1] = value;
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }

    private static bool CurveEquals(FanTable readback, IReadOnlyList<CurvePoint> curve) =>
        DecodeCurve(readback).SequenceEqual(curve);

    private static bool SnapshotEquals(FanSnapshot left, FanSnapshot right) =>
        left.Left.DutyBuffer.SequenceEqual(right.Left.DutyBuffer)
        && left.Left.TemperatureBuffer.SequenceEqual(right.Left.TemperatureBuffer)
        && left.Right.DutyBuffer.SequenceEqual(right.Right.DutyBuffer)
        && left.Right.TemperatureBuffer.SequenceEqual(right.Right.TemperatureBuffer)
        && left.CustomFlag == right.CustomFlag
        && left.FullSpeedFlag == right.FullSpeedFlag;

    private static int DecodeRpm(byte high, byte low)
    {
        var divisor = (high << 8) | low;
        return divisor == 0 ? 0 : 480_000 / divisor;
    }

    private static bool Flag(byte value) => (value & 0x80) != 0;
}

internal sealed class ClawA2VmLightingCapability(IClawMcuTransport transport)
{
    private static readonly TimeSpan MinimumPersistentWriteInterval = TimeSpan.FromSeconds(1);
    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private LightingState? _observed;
    private byte[]? _observedProfile;
    private DateTimeOffset _lastPersistentWrite;

    public async ValueTask<LightingState> ReadAsync(CancellationToken cancellationToken)
    {
        var profile = await _transport.ReadProfileAsync(
            ClawHardwareFacts.LightingProfileAddress,
            32,
            cancellationToken).ConfigureAwait(false);
        if (profile.Length != 32 || profile[1] is not 1 || profile[2] is not 0x09)
        {
            throw new InvalidOperationException("The committed A2VM lighting profile has an unrecognized shape.");
        }

        _observedProfile = [.. profile];
        _observed = new LightingState(
            profile[4],
            ReadColor(profile, 5),
            ReadColor(profile, 17),
            ReadColor(profile, 29));
        return _observed;
    }

    public async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        Func<LightingState, LightingState> update,
        CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var wanted = update(before);
        if (wanted == before)
        {
            return VerifiedValue(command, before);
        }

        if (wanted.Brightness is < 0 or > 100
            || !IsColor(wanted.RightRingColor)
            || !IsColor(wanted.LeftRingColor)
            || !IsColor(wanted.ButtonsColor))
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.ValueOutOfRange,
                "Lighting brightness or colour is outside the validated range.");
        }

        var untilNextWrite = MinimumPersistentWriteInterval
                             - (DateTimeOffset.UtcNow - _lastPersistentWrite);
        if (untilNextWrite > TimeSpan.Zero)
        {
            if (DateTimeOffset.UtcNow + untilNextWrite >= command.Deadline)
            {
                return ClawResults.Rejected(
                    command,
                    new CapabilityReason(
                        CapabilityReasonCode.Quiescing,
                        "The lighting command deadline is too short for the persistent-write interval.",
                        Retryable: true));
            }

            await Task.Delay(untilNextWrite, cancellationToken).ConfigureAwait(false);
            before = await ReadAsync(cancellationToken).ConfigureAwait(false);
            wanted = update(before);
            if (wanted == before)
            {
                return VerifiedValue(command, before);
            }
        }

        if (!ClawWriteBudget.IsAvailable(command.Deadline))
        {
            return ClawResults.Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "Insufficient command budget for a persistent lighting write.");
        }

        // The exact bytes the device held before this command, kept for the rollback below. This
        // profile survives a reboot, so a write that lands only partly — or lands normalized into
        // something the user did not choose — would otherwise stay on the hardware permanently
        // while the UI reported the command as failed.
        byte[] restore = _observedProfile is { Length: 32 }
            ? [.. _observedProfile]
            : throw new InvalidOperationException("The lighting profile snapshot was lost.");
        var payload = Encode(wanted, restore);
        _lastPersistentWrite = DateTimeOffset.UtcNow;
        LightingState readback;
        try
        {
            await _transport.WriteProfileAsync(
                ClawHardwareFacts.LightingProfileAddress,
                payload,
                cancellationToken).ConfigureAwait(false);
            readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation can arrive after WriteProfile has reached persistent MCU storage. Finish
            // the bounded safety rollback with an independent token before reporting the command.
            var rollback = await RollbackAsync(before, restore, CancellationToken.None)
                .ConfigureAwait(false);
            PluginTrace.Failure("lighting", "Persistent lighting write was cancelled", exception);
            return ClawResults.Indeterminate(
                command,
                CapabilityReasonCode.Quiescing,
                "The persistent lighting write was cancelled after application began.",
                rollback);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The write may have reached the MCU before it failed, so the device cannot be assumed
            // untouched: restore explicitly rather than hoping nothing happened.
            var rollback = await RollbackAsync(before, restore, CancellationToken.None)
                .ConfigureAwait(false);
            PluginTrace.Failure("lighting", "Persistent lighting write failed", exception);
            return ClawResults.Indeterminate(
                command,
                CapabilityReasonCode.TransportFaulted,
                $"The persistent lighting write failed: {exception.Message}",
                rollback);
        }

        if (readback == wanted && _observedProfile is not null && _observedProfile.SequenceEqual(payload))
        {
            return VerifiedValue(command, readback);
        }

        var mismatchRollback = await RollbackAsync(before, restore, CancellationToken.None)
            .ConfigureAwait(false);
        return ClawResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            "Persistent lighting readback did not match the committed profile.",
            mismatchRollback);
    }

    /// <summary>Restores the exact profile the device held before an unverified write.</summary>
    /// <param name="before">The state that profile represented.</param>
    /// <param name="restore">Its exact 32 bytes, encoded before the write.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>What the rollback achieved, as the command result reports it.</returns>
    /// <remarks>
    /// Verified by reading back, because an unverified rollback is the same problem one step later.
    /// The rate limit is deliberately not consulted here: it exists to stop a user's slider from
    /// hammering the MCU, and refusing to undo a bad write because the last one was recent is how
    /// the unintended profile would become permanent.
    /// </remarks>
    private async ValueTask<RollbackResult> RollbackAsync(
        LightingState before,
        byte[] restore,
        CancellationToken cancellationToken)
    {
        try
        {
            await _transport.WriteProfileAsync(
                ClawHardwareFacts.LightingProfileAddress,
                restore,
                cancellationToken).ConfigureAwait(false);
            _lastPersistentWrite = DateTimeOffset.UtcNow;
            var restored = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (restored == before
                && _observedProfile is not null
                && _observedProfile.SequenceEqual(restore))
            {
                PluginTrace.Info("lighting", "Unverified lighting write was rolled back.");
                return RollbackResult.RestoredVerified;
            }

            PluginTrace.Warn(
                "lighting",
                "The lighting profile could not be restored; the device holds an unintended "
                + "profile that persists across reboot.");
            return RollbackResult.RestoreFailed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PluginTrace.Failure("lighting", "Lighting rollback failed", exception);
            return RollbackResult.RestoreFailed;
        }
    }

    internal static byte[] Encode(LightingState state)
    {
        var payload = new byte[32];
        payload[0] = 0;
        payload[1] = 1;
        payload[2] = 0x09;
        payload[3] = 0x03;
        return Encode(state, payload);
    }

    private static byte[] Encode(LightingState state, byte[] template)
    {
        if (template.Length != 32)
        {
            throw new ArgumentException("A Claw lighting profile must contain exactly 32 bytes.", nameof(template));
        }

        byte[] payload = [.. template];
        payload[4] = checked((byte)state.Brightness);
        for (var zone = 0; zone < 4; zone++)
        {
            WriteColor(payload, 5 + zone * 3, state.RightRingColor);
            WriteColor(payload, 17 + zone * 3, state.LeftRingColor);
        }

        WriteColor(payload, 29, state.ButtonsColor);
        return payload;
    }

    private static CapabilityCommandResult VerifiedValue(
        CapabilityCommand command,
        LightingState state)
    {
        var currentValue = command.CapabilityId == CapabilityIds.LightingBrightness
            ? CapabilityValue.Integer(state.Brightness)
            : CapabilityValue.Color(command.InstanceId switch
            {
                CapabilityInstances.RightRing => state.RightRingColor,
                CapabilityInstances.LeftRing => state.LeftRingColor,
                CapabilityInstances.Buttons => state.ButtonsColor,
                _ => throw new InvalidOperationException("Unknown lighting zone.")
            });
        return ClawResults.Verified(command, currentValue);
    }

    private static int ReadColor(byte[] payload, int offset) =>
        (payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2];

    private static void WriteColor(byte[] payload, int offset, int color)
    {
        payload[offset] = checked((byte)((color >> 16) & 0xFF));
        payload[offset + 1] = checked((byte)((color >> 8) & 0xFF));
        payload[offset + 2] = checked((byte)(color & 0xFF));
    }

    private static bool IsColor(int color) => color is >= 0 and <= 0xFFFFFF;
}

internal static class CapabilityIds
{
    public const string PowerSustained = "power.primary-limit";
    public const string PowerBoost = "power.boost-limit";
    public const string ChargeLimit = "battery.charge-limit";
    public const string Scenario = "power.scenario";
    public const string FanMode = "fan.mode";
    public const string FanCurve = "fan.curve";
    public const string FanRpm = "fan.measured-rpm";
    public const string Temperature = "telemetry.temperature";
    public const string LightingBrightness = "lighting.brightness";
    public const string LightingColor = "lighting.zone-color";
    public const string Controller = "controller.source";
    public const string Motion = "motion.source";
    public const string Rumble = "haptic.rumble";
    public const string VariableRefreshRate = "display.variable-refresh";

    /// <summary>Whether Intel Endurance Gaming engages: off, on, or left to the driver.</summary>
    public const string EnduranceGaming = "display.endurance-gaming";

    /// <summary>The frame target Endurance Gaming holds to while engaged.</summary>
    public const string EnduranceGamingMode = "display.endurance-gaming-mode";

    /// <summary>Whether the graphics driver downloads prebuilt shaders for games.</summary>
    public const string ShaderDownload = "display.shader-download";

    /// <summary>The share of system memory the integrated GPU may use, as a percentage.</summary>
    public const string SharedGpuMemory = "display.shared-gpu-memory";

    /// <summary>Driver-level frame presentation: what "driver VSync" is on an Intel part.</summary>
    public const string DriverVsync = "display.driver-vsync";
}

internal static class CapabilityInstances
{
    public const string Left = "left";
    public const string Right = "right";
    public const string RightRing = "right-ring";
    public const string LeftRing = "left-ring";
    public const string Buttons = "buttons";
}

/// <summary>Overlay sections this plugin declares in its descriptor set.</summary>
internal static class SectionIds
{
    public const string Power = DeviceSections.PowerId;
    public const string Lighting = DeviceSections.RgbId;

    /// <summary>Read-only ownership and telemetry. Nothing here is a control.</summary>
    /// <remarks>
    /// Was <c>input</c>, a page of three rows that only ever reported whether the plugin held the
    /// pad, the gyro and the rumble sink. That is worth reading when something is wrong and worth
    /// nothing the rest of the time, so it is no longer a Controller page competing with the one
    /// that has the actual controller settings on it.
    /// </remarks>
    public const string Info = DeviceSections.InfoId;
}

/// <summary>Categories inside the declared overlay sections.</summary>
internal static class CategoryIds
{
    public const string Limits = "limits";
    public const string Charging = "charging";
    public const string Control = "control";
    public const string Readings = "readings";
    public const string Zones = "zones";
    public const string Ownership = "ownership";
}
