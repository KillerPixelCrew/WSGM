// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>The three package power limits and the performance mode, as last read.</summary>
/// <param name="Sustained">SPL in watts, or null when the firmware does not report it.</param>
/// <param name="Slow">SPPT in watts, or null.</param>
/// <param name="Fast">FPPT in watts, or null.</param>
/// <param name="Mode">Performance mode (0 performance, 1 turbo, 2 silent), or null.</param>
internal sealed record AllyPowerState(int? Sustained, int? Slow, int? Fast, int? Mode)
{
    public bool LimitsReadable => Sustained is not null && Slow is not null && Fast is not null;
}

/// <summary>The fan curves captured together, one per channel the firmware exposes.</summary>
internal sealed record AllyFanSnapshot(byte[]? Cpu, byte[]? Gpu, byte[]? Mid, int? Mode)
{
    public bool Readable => Cpu is not null && Gpu is not null;
}

/// <summary>Power limits and performance mode over ATKACPI.</summary>
/// <remarks>
///     HC writes SPL on its long limit and SPPT plus FPPT together on its short limit
///     (<c>AsusACPI.cs:343-352</c>); HHD writes FPPT, then SPPT, then SPL, all to the same value when
///     boost is off (<c>adjustor/drivers/asus/__init__.py:384-389</c>). This keeps HC's two-limit model,
///     so the boost descriptor drives SPPT and FPPT as one value, and orders every write so that
///     SPL &lt;= SPPT &lt;= FPPT holds after each step, which is the order the Ally X Lab restore used.
/// </remarks>
internal sealed class AllyPowerCapability(IAsusAcpi acpi, AllyModel model)
{
    private readonly IAsusAcpi _acpi = acpi ?? throw new ArgumentNullException(nameof(acpi));
    private readonly AllyModel _model = model ?? throw new ArgumentNullException(nameof(model));

    private AllyPowerState _written = new(null, null, null, null);

    /// <summary>HHD's <c>TDP_DELAY</c> between limit writes.</summary>
    internal static TimeSpan WriteSpacing { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The settle time the Ally X Lab gave a performance-mode change before reading back.</summary>
    internal static TimeSpan ModeSettle { get; set; } = TimeSpan.FromMilliseconds(150);

    public int Minimum => _model.MinimumWatts;

    public int Maximum => _model.MaximumWatts;

    /// <summary>What the firmware reports. A limit it reports as 0 W, as the Xbox Ally X does, is unknown.</summary>
    public AllyPowerState Read()
    {
        return new AllyPowerState(
            Watts(AsusAcpiId.SustainedPower),
            Watts(AsusAcpiId.SlowPower),
            Watts(AsusAcpiId.FastPower),
            Scalar(AsusAcpiId.PerformanceMode) is { } mode && AllyModels.ScenarioName(mode) is not null
                ? mode
                : null);
    }

    /// <summary>The firmware's report, with anything it cannot report filled from the last value written.</summary>
    /// <remarks>
    ///     HC never reads these back and simply writes (<c>ROGAlly.cs:694-702</c>). Where the firmware is
    ///     silent, what this cycle last wrote is the best available statement of the device's state.
    /// </remarks>
    public AllyPowerState Effective()
    {
        var read = Read();
        return new AllyPowerState(
            read.Sustained ?? _written.Sustained,
            read.Slow ?? _written.Slow,
            read.Fast ?? _written.Fast,
            read.Mode ?? _written.Mode);
    }

    public async ValueTask<CapabilityCommandResult> ApplySustainedAsync(
        CapabilityCommand command,
        int watts,
        CancellationToken cancellationToken)
    {
        if (!InRange(watts))
        {
            return OutOfRange(command);
        }

        var before = Effective();
        // A paired command moves every limit to the one target, as HHD does with boost off. A plain
        // sustained change carries the boost pair up only when it would otherwise sit below SPL.
        var slow = command.ApplyPowerPair ? watts : Math.Max(before.Slow ?? watts, watts);
        var fast = command.ApplyPowerPair ? watts : Math.Max(before.Fast ?? watts, slow);
        return await ApplyLimitsAsync(command, before, watts, Math.Min(slow, Maximum), Math.Min(fast, Maximum),
            watts, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CapabilityCommandResult> ApplyBoostAsync(
        CapabilityCommand command,
        int watts,
        CancellationToken cancellationToken)
    {
        if (!InRange(watts))
        {
            return OutOfRange(command);
        }

        var before = Effective();
        // A boost ceiling below the current sustained limit pulls SPL down with it, the Claw rule.
        var sustained = Math.Min(before.Sustained ?? watts, watts);
        return await ApplyLimitsAsync(command, before, sustained, watts, watts, watts, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CapabilityCommandResult> ApplyScenarioAsync(
        CapabilityCommand command,
        string scenario,
        CancellationToken cancellationToken)
    {
        byte target;
        try
        {
            target = AllyModels.ScenarioValue(scenario);
        }
        catch (ArgumentOutOfRangeException)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, "Unknown performance mode.");
        }

        var before = Read();
        try
        {
            if (before.Mode != target)
            {
                _ = _acpi.Write(AsusAcpiId.PerformanceMode, target);
                // A mode change resets the limits to the mode's own, which a silent firmware does not report.
                _written = new AllyPowerState(null, null, null, null);
                await Task.Delay(ModeSettle, cancellationToken).ConfigureAwait(false);
            }

            var after = Read();
            if (after.Mode == target)
            {
                _written = _written with { Mode = target };
                return AllyResults.Verified(command, CapabilityValue.Choice(scenario));
            }

            if (after.Mode is null)
            {
                _written = _written with { Mode = target };
                return AllyResults.Unverified(command, "The firmware does not report its performance mode.");
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            PluginTrace.Failure("power", "Performance mode write failed", ex);
        }

        var rollback = before.Mode is { } original
            ? await RestoreModeAsync(original, CancellationToken.None).ConfigureAwait(false)
            : RollbackResult.RestoreFailed;
        return AllyResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
            "Performance mode readback did not match.", rollback);
    }

    /// <summary>Restores a captured state: mode first, since a mode change resets the limits.</summary>
    public async ValueTask<bool> RestoreAsync(AllyPowerState original, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Mode is { } mode
            && await RestoreModeAsync(mode, cancellationToken).ConfigureAwait(false) is not RollbackResult
                .RestoredVerified)
        {
            return false;
        }

        if (!original.LimitsReadable)
        {
            return false;
        }

        var current = Read();
        await WriteOrderedAsync(current, original.Sustained!.Value, original.Slow!.Value, original.Fast!.Value,
            cancellationToken).ConfigureAwait(false);
        var readback = Read();
        return readback.Sustained == original.Sustained && readback.Slow == original.Slow
                                                        && readback.Fast == original.Fast;
    }

    /// <summary>The write order that keeps SPL &lt;= SPPT &lt;= FPPT after every step.</summary>
    /// <remarks>
    ///     Ported from the retired Ally X Lab's reviewed restore (<c>tools/AllyXLab/AsusControl.cs</c>,
    ///     <c>Restore</c>, removed in <c>829c5a5c</c>). With the current
    ///     limits unknown, HHD's fixed fast-slow-steady order is used.
    /// </remarks>
    internal static IReadOnlyList<(AsusAcpiId Id, int Watts)> WriteOrder(
        AllyPowerState current,
        int sustained,
        int slow,
        int fast)
    {
        (AsusAcpiId, int) spl = (AsusAcpiId.SustainedPower, sustained);
        (AsusAcpiId, int) sppt = (AsusAcpiId.SlowPower, slow);
        (AsusAcpiId, int) fppt = (AsusAcpiId.FastPower, fast);
        if (!current.LimitsReadable)
        {
            return [fppt, sppt, spl];
        }

        if (fast < current.Slow)
        {
            return [spl, sppt, fppt];
        }

        return slow < current.Sustained ? [fppt, spl, sppt] : [fppt, sppt, spl];
    }

    private async ValueTask<CapabilityCommandResult> ApplyLimitsAsync(
        CapabilityCommand command,
        AllyPowerState before,
        int sustained,
        int slow,
        int fast,
        int reported,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteOrderedAsync(before, sustained, slow, fast, cancellationToken).ConfigureAwait(false);
            var readback = Read();
            if (!readback.LimitsReadable)
            {
                _written = _written with { Sustained = sustained, Slow = slow, Fast = fast };
                return AllyResults.Unverified(command, "The firmware does not report its package power limits.");
            }

            if (readback.Sustained == sustained && readback.Slow == slow && readback.Fast == fast)
            {
                _written = _written with { Sustained = sustained, Slow = slow, Fast = fast };
                return AllyResults.Verified(command, CapabilityValue.Integer(reported));
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            // At least one limit may have reached firmware. Fall through to the captured-state rollback.
            PluginTrace.Failure("power", "Power limit write failed", ex);
        }

        var rollback = await TryRestoreLimitsAsync(before).ConfigureAwait(false);
        return AllyResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
            "Power limit readback did not match the requested limits.", rollback);
    }

    private async ValueTask WriteOrderedAsync(
        AllyPowerState current,
        int sustained,
        int slow,
        int fast,
        CancellationToken cancellationToken)
    {
        var first = true;
        foreach (var (id, watts) in WriteOrder(current, sustained, slow, fast))
        {
            if (Current(current, id) == watts)
            {
                continue;
            }

            if (!first)
            {
                await Task.Delay(WriteSpacing, cancellationToken).ConfigureAwait(false);
            }

            first = false;
            _ = _acpi.Write(id, checked((uint)watts));
        }
    }

    private async ValueTask<RollbackResult> TryRestoreLimitsAsync(AllyPowerState before)
    {
        if (!before.LimitsReadable)
        {
            return RollbackResult.RestoreFailed;
        }

        try
        {
            await WriteOrderedAsync(Read(), before.Sustained!.Value, before.Slow!.Value, before.Fast!.Value,
                CancellationToken.None).ConfigureAwait(false);
            var readback = Read();
            return readback.Sustained == before.Sustained && readback.Slow == before.Slow
                                                          && readback.Fast == before.Fast
                ? RollbackResult.RestoredVerified
                : RollbackResult.RestoredUnverified;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            PluginTrace.Failure("power", "Power limit rollback failed", ex);
            return RollbackResult.RestoreFailed;
        }
    }

    private async ValueTask<RollbackResult> RestoreModeAsync(int mode, CancellationToken cancellationToken)
    {
        try
        {
            if (Scalar(AsusAcpiId.PerformanceMode) != mode)
            {
                _ = _acpi.Write(AsusAcpiId.PerformanceMode, checked((uint)mode));
                await Task.Delay(ModeSettle, cancellationToken).ConfigureAwait(false);
            }

            return Scalar(AsusAcpiId.PerformanceMode) == mode
                ? RollbackResult.RestoredVerified
                : RollbackResult.RestoredUnverified;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            PluginTrace.Failure("power", "Performance mode restore failed", ex);
            return RollbackResult.RestoreFailed;
        }
    }

    private static int? Current(AllyPowerState state, AsusAcpiId id)
    {
        return id switch
        {
            AsusAcpiId.SustainedPower => state.Sustained,
            AsusAcpiId.SlowPower => state.Slow,
            _ => state.Fast
        };
    }

    private int? Scalar(AsusAcpiId id)
    {
        return AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(id), out var value) ? value : null;
    }

    private int? Watts(AsusAcpiId id)
    {
        return Scalar(id) is { } watts && AllyRecoveryJournal.IsValidWatts(watts) ? watts : null;
    }

    private bool InRange(int watts)
    {
        return watts >= Minimum && watts <= Maximum;
    }

    private CapabilityCommandResult OutOfRange(CapabilityCommand command)
    {
        return AllyResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
            $"The {_model.DisplayName} accepts {Minimum}-{Maximum} W.");
    }
}

/// <summary>Battery charge ceiling over ATKACPI.</summary>
/// <remarks>HC writes 0-100 through DEVS 0x00120057 (<c>AsusACPI.cs:335-341</c>).</remarks>
internal sealed class AllyChargeLimitCapability(IAsusAcpi acpi)
{
    /// <summary>
    ///     Lower bound offered to the user. HC accepts 0-100; a ceiling below 40 % is refused here so a
    ///     slip of the slider cannot leave the device unable to charge. The lab report may widen it.
    /// </summary>
    internal const int MinimumPercent = 40;

    internal const int MaximumPercent = 100;

    private readonly IAsusAcpi _acpi = acpi ?? throw new ArgumentNullException(nameof(acpi));

    public int? Read()
    {
        return AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(AsusAcpiId.ChargeLimit), out var value)
               && value is >= 0 and <= 100
            ? value
            : null;
    }

    public CapabilityCommandResult Apply(CapabilityCommand command, int percent)
    {
        if (percent is < MinimumPercent or > MaximumPercent)
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                $"The charge limit must be {MinimumPercent}-{MaximumPercent} %.");
        }

        var before = Read();
        try
        {
            _ = _acpi.Write(AsusAcpiId.ChargeLimit, (uint)percent);
            var readback = Read();
            if (readback == percent)
            {
                return AllyResults.Verified(command, CapabilityValue.Integer(percent));
            }

            if (readback is null)
            {
                return AllyResults.Unverified(command, "The firmware does not report the charge limit.");
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            PluginTrace.Failure("charge-limit", "Charge limit write failed", ex);
        }

        var rollback = RollbackResult.RestoreFailed;
        if (before is { } original)
        {
            try
            {
                _ = _acpi.Write(AsusAcpiId.ChargeLimit, (uint)original);
                rollback = Read() == original ? RollbackResult.RestoredVerified : RollbackResult.RestoredUnverified;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            {
                PluginTrace.Failure("charge-limit", "Charge limit rollback failed", ex);
            }
        }

        return AllyResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
            "Charge limit readback did not match.", rollback);
    }
}

/// <summary>Fan curves over ATKACPI.</summary>
/// <remarks>
///     HC converts its curve to sixteen bytes, eight temperatures then eight duties, clamps duties to
///     99 and writes the same curve to the CPU, GPU and mid fans (<c>ROGAlly.cs:287-327</c>,
///     <c>AsusACPI.cs:281-298</c>). Turning custom control off writes HC's default tables back
///     (<c>ROGAlly.cs:466-478</c>); this plugin writes the curves it captured first when it has them.
/// </remarks>
internal sealed class AllyFanCapability(IAsusAcpi acpi)
{
    internal const int PointCount = 8;
    internal const int MinimumTemperature = 20;
    internal const int MaximumTemperature = 110;

    /// <summary>HC's <c>defaultCPUFan</c>, also written to the mid fan (<c>ROGAlly.cs:185-189</c>).</summary>
    internal static readonly byte[] DefaultCpuCurve = [58, 61, 64, 68, 72, 77, 81, 98, 8, 17, 22, 26, 34, 41, 48, 69];

    /// <summary>HC's <c>defaultGPUFan</c> (<c>ROGAlly.cs:191-195</c>).</summary>
    internal static readonly byte[] DefaultGpuCurve = [58, 61, 64, 68, 72, 77, 81, 98, 12, 22, 29, 31, 38, 45, 52, 74];

    private readonly IAsusAcpi _acpi = acpi ?? throw new ArgumentNullException(nameof(acpi));

    /// <summary>Whether the mid fan curve answered with a valid curve when the service started.</summary>
    public bool HasMidFan { get; private set; }

    /// <summary>Whether the firmware answered any curve query when the service started.</summary>
    public bool CurvesReadable { get; private set; }

    /// <summary>The CPU curve this cycle last wrote, for firmware that cannot report it.</summary>
    public byte[]? WrittenCpu { get; private set; }

    public void Probe()
    {
        var mode = Mode();
        HasMidFan = ReadCurve(AsusAcpiId.MidFanCurve, mode) is not null;
        CurvesReadable = HasMidFan || ReadCurve(AsusAcpiId.CpuFanCurve, mode) is not null;
    }

    public AllyFanSnapshot Read()
    {
        var mode = Mode();
        return new AllyFanSnapshot(
            ReadCurve(AsusAcpiId.CpuFanCurve, mode),
            ReadCurve(AsusAcpiId.GpuFanCurve, mode),
            HasMidFan ? ReadCurve(AsusAcpiId.MidFanCurve, mode) : null,
            mode);
    }

    /// <summary>The CPU and GPU fan readings, in HC's duty units.</summary>
    public (int? Cpu, int? Gpu) ReadFans()
    {
        return (Scalar(AsusAcpiId.CpuFanSpeed), Scalar(AsusAcpiId.GpuFanSpeed));
    }

    public static IReadOnlyList<CurvePoint> Decode(byte[] curve)
    {
        return [.. Enumerable.Range(0, PointCount).Select(index => new CurvePoint(curve[index], curve[8 + index]))];
    }

    /// <summary>Encodes eight semantic points as the firmware's sixteen bytes.</summary>
    public static bool TryEncode(IReadOnlyList<CurvePoint> points, out byte[] curve, out string? error)
    {
        curve = new byte[AsusAcpiProtocol.CurveLength];
        if (points.Count != PointCount)
        {
            error = "ASUS fan curves have exactly eight points.";
            return false;
        }

        for (var index = 0; index < PointCount; index++)
        {
            var point = points[index];
            if (point.Input is < MinimumTemperature or > MaximumTemperature || point.Output is < 0 or > 100)
            {
                error = $"Fan points must be {MinimumTemperature}-{MaximumTemperature} °C and 0-100 %.";
                return false;
            }

            if (index > 0 && (point.Input <= points[index - 1].Input || point.Output < points[index - 1].Output))
            {
                error = "Fan-curve temperatures must rise and duties must not fall.";
                return false;
            }

            curve[index] = (byte)point.Input;
            // HC clamps every duty to 99 before writing (AsusACPI.SetFanCurve).
            curve[8 + index] = (byte)Math.Min(point.Output, 99);
        }

        if (!AsusAcpiProtocol.IsValidCurve(curve))
        {
            error = "The fan curve needs at least one non-zero duty.";
            return false;
        }

        error = null;
        return true;
    }

    public async ValueTask<CapabilityCommandResult> ApplyCurveAsync(
        CapabilityCommand command,
        IReadOnlyList<CurvePoint> points,
        CancellationToken cancellationToken)
    {
        if (!TryEncode(points, out var curve, out var error))
        {
            return AllyResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
        }

        return await WriteAllAsync(command, curve, curve, CapabilityValue.Curve([.. Decode(curve)]),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the fans to firmware control: the captured curves, else HC's defaults.</summary>
    public ValueTask<CapabilityCommandResult> ApplyAutomaticAsync(
        CapabilityCommand command,
        AllyFanSnapshot? original,
        CancellationToken cancellationToken)
    {
        var cpu = original?.Cpu ?? DefaultCpuCurve;
        var gpu = original?.Gpu ?? DefaultGpuCurve;
        return WriteAllAsync(command, cpu, gpu, CapabilityValue.Choice(FanModes.Automatic), cancellationToken,
            original?.Mid);
    }

    /// <summary>Writes HC's factory tables, the state HC returns the fans to (<c>ROGAlly.cs:466-478</c>).</summary>
    public async ValueTask WriteFactoryAsync(CancellationToken cancellationToken)
    {
        await WriteChannelsAsync(DefaultCpuCurve, DefaultGpuCurve, DefaultCpuCurve, cancellationToken)
            .ConfigureAwait(false);
        WrittenCpu = DefaultCpuCurve;
    }

    public async ValueTask<bool> RestoreAsync(AllyFanSnapshot original, CancellationToken cancellationToken)
    {
        if (!original.Readable)
        {
            return false;
        }

        await WriteChannelsAsync(original.Cpu!, original.Gpu!, original.Mid, cancellationToken)
            .ConfigureAwait(false);
        var readback = Read();
        return Same(readback.Cpu, original.Cpu) && Same(readback.Gpu, original.Gpu)
                                                && (original.Mid is null || Same(readback.Mid, original.Mid));
    }

    private async ValueTask<CapabilityCommandResult> WriteAllAsync(
        CapabilityCommand command,
        byte[] cpu,
        byte[] gpu,
        CapabilityValue reported,
        CancellationToken cancellationToken,
        byte[]? mid = null)
    {
        var before = Read();
        try
        {
            await WriteChannelsAsync(cpu, gpu, mid ?? cpu, cancellationToken).ConfigureAwait(false);
            WrittenCpu = cpu;
            var readback = Read();
            if (Same(readback.Cpu, cpu) && Same(readback.Gpu, gpu)
                                        && (!HasMidFan || Same(readback.Mid, mid ?? cpu)))
            {
                return AllyResults.Verified(command, reported);
            }

            if (!CurvesReadable)
            {
                // HC never reads curves back (ROGAlly.cs:313-321); a firmware that refuses the query
                // leaves the write unverified, which is not a failure.
                return AllyResults.Unverified(command, "The firmware does not report its fan curves.");
            }

            // DSTS may report the firmware's table for the mode rather than the curve now in force; the
            // Ally X Lab never established which. A mismatch is therefore unverified, not a failure.
            PluginTrace.Warn("fans", "Fan-curve readback differs from what was written; reporting it unverified.");
            return AllyResults.Unverified(command, "The firmware's fan-curve readback did not reflect the write.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            PluginTrace.Failure("fans", "Fan-curve write failed", ex);
        }

        var rollback = RollbackResult.RestoreFailed;
        try
        {
            if (before.Readable && await RestoreAsync(before, CancellationToken.None).ConfigureAwait(false))
            {
                rollback = RollbackResult.RestoredVerified;
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            PluginTrace.Failure("fans", "Fan-curve rollback failed", ex);
        }

        return AllyResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
            "The fan-curve write failed.", rollback);
    }

    private async ValueTask WriteChannelsAsync(
        byte[] cpu,
        byte[] gpu,
        byte[]? mid,
        CancellationToken cancellationToken)
    {
        _ = _acpi.WriteBuffer(AsusAcpiId.CpuFanCurve, cpu);
        await Task.Delay(AllyPowerCapability.WriteSpacing, cancellationToken).ConfigureAwait(false);
        _ = _acpi.WriteBuffer(AsusAcpiId.GpuFanCurve, gpu);
        // HC writes the mid fan on every Ally (ROGAlly.cs:317-321). Where the probe could not tell,
        // do the same, and let a firmware without the channel refuse it without failing the rest.
        if (mid is not null && (HasMidFan || !CurvesReadable))
        {
            await Task.Delay(AllyPowerCapability.WriteSpacing, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = _acpi.WriteBuffer(AsusAcpiId.MidFanCurve, mid);
            }
            catch (Exception ex) when (!HasMidFan && ex is IOException or Win32Exception)
            {
                PluginTrace.Change("fans", "mid-write", $"Mid fan curve refused ({ex.Message}).",
                    DeviceTraceLevel.Warn);
            }
        }
    }

    private byte[]? ReadCurve(AsusAcpiId id, int? mode)
    {
        var curve = _acpi.ReadBuffer(id, AsusAcpiProtocol.CurveSelector(mode ?? 0));
        return AsusAcpiProtocol.IsValidCurve(curve) ? curve : null;
    }

    private int? Mode()
    {
        return Scalar(AsusAcpiId.PerformanceMode);
    }

    private int? Scalar(AsusAcpiId id)
    {
        return AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(id), out var value) ? value : null;
    }

    private static bool Same(byte[]? left, byte[]? right)
    {
        return left is not null && right is not null && left.AsSpan().SequenceEqual(right);
    }
}

internal static class FanModes
{
    public const string Automatic = "automatic";
    public const string Custom = "custom";
}
