using System.Text;
using LibreHardwareMonitor.Hardware;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Core;

public sealed class RtssOsdContentTests
{
    private static readonly RtssOsdMetrics FullMetrics = RtssOsdMetrics.Empty with
    {
        CpuLoadPercent = 42.5,
        CpuPowerWatts = 13.7,
        CpuTemperatureC = 61,
        GpuLoadPercent = 55.2,
        GpuPowerWatts = 9.8,
        GpuMemoryUsedGb = 1.2,
        GpuMemoryTotalGb = 18.0,
        MemoryUsedGb = 12.3,
        MemoryTotalGb = 31.6,
        BatteryPercent = 76,
        BatteryWatts = -14.2,
        BatteryMinutesRemaining = 95,
        OnAcPower = false
    };

    private static readonly RtssOsdMetrics EmptyMetrics = RtssOsdMetrics.Empty with
    {
        OnAcPower = true
    };

    [Fact]
    public void Minimal_IsOneRowOfRtssFramerate()
    {
        var text = RtssOsdContent.Build(1, EmptyMetrics);

        Assert.StartsWith("<C0=FFFFFF><C1=8000FF>", text);
        Assert.Contains("<FR>", text);
        Assert.Contains("<APP>", text);
        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("<FT>", text);
    }

    [Fact]
    public void Minimal_DoesNotShowPowerStatus()
    {
        var power = new RtssOsdPowerStatus(18, true, true, 17, "Raising");

        var text = RtssOsdContent.Build(1, FullMetrics, power);

        Assert.DoesNotContain("TDP", text);
        Assert.DoesNotContain("RAISING", text);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RichLevels_ShowTdpAndLiveAutoTdpActivity(int level)
    {
        var power = new RtssOsdPowerStatus(18, true, true, 17, "Raising");

        var text = RtssOsdContent.Build(level, FullMetrics, power);

        Assert.Contains("TDP", text);
        Assert.Contains("18<S1>W", text);
        Assert.Contains("AUTO TDP", text);
        Assert.Contains("17<S1>W", text);
        Assert.Contains("RAISING", text);
    }

    [Fact]
    public void Tdp_ShowsPublishedBoostLimitsAfterSustained()
    {
        var power = new RtssOsdPowerStatus(25, false, false, null, string.Empty, SlowWatts: 37);

        var text = RtssOsdContent.Build(2, FullMetrics, power);

        Assert.Contains("<C0>25/37<S1>W", text);
    }

    [Fact]
    public void AutoTdpIndicator_IsHiddenWhileAutoTdpIsOff()
    {
        var power = new RtssOsdPowerStatus(18, false, false, null, string.Empty);

        var text = RtssOsdContent.Build(2, FullMetrics, power);

        Assert.Contains("TDP", text);
        Assert.DoesNotContain("AUTO TDP", text);
    }

    [Fact]
    public void AutoTdpIndicator_IsHiddenUntilAutoTdpIsActivelyControlling()
    {
        var power = new RtssOsdPowerStatus(18, true, false, null, string.Empty);

        var text = RtssOsdContent.Build(2, FullMetrics, power);

        Assert.Contains("TDP", text);
        Assert.DoesNotContain("AUTO TDP", text);
    }

    [Fact]
    public void Extended_IsOneRowWithEverySourcedSubject()
    {
        var text = RtssOsdContent.Build(2, FullMetrics);

        Assert.DoesNotContain("\n", text);
        Assert.Contains("<FR>", text);
        Assert.Contains("<FT>", text);
        Assert.Contains("GPU", text);
        Assert.Contains("VRAM", text);
        Assert.Contains("CPU", text);
        Assert.Contains("RAM", text);
        Assert.Contains("BATT", text);
        // Entries are joined with HC's separator.
        Assert.Contains("<C1> | <C>", text);
    }

    [Fact]
    public void Full_IsOneRowPerSubject()
    {
        var text = RtssOsdContent.Build(3, FullMetrics);

        var rows = text.Split('\n');
        Assert.Equal(6, rows.Length);
        // Full shows used/total memory, temperatures and the discharge estimate.
        Assert.Contains("12.3/31.6", text);
        Assert.Contains("1.2/18.0", text);
        Assert.Contains("61<S1>C", text);
        Assert.Contains("01<S1>h", text);
        Assert.Contains("35<S1>min", text);
    }

    [Fact]
    public void SubjectsWithoutASourceDoNotRender()
    {
        var text = RtssOsdContent.Build(3, EmptyMetrics);

        // Only the FPS row survives: its tags are filled by RTSS, not by a sensor.
        Assert.DoesNotContain("GPU", text);
        Assert.DoesNotContain("CPU", text);
        Assert.DoesNotContain("RAM", text);
        Assert.DoesNotContain("BATT", text);
        Assert.Contains("<FR>", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void BatteryTimeIsOmittedOnAcPower()
    {
        var charging = FullMetrics with { OnAcPower = true };

        var text = RtssOsdContent.Build(3, charging);

        Assert.DoesNotContain("<S1>h", text);
        Assert.DoesNotContain("<S1>min", text);
        Assert.Contains("BATT", text);
    }

    [Fact]
    public void UnrenderedLevelsAreEmpty()
    {
        Assert.Equal(string.Empty, RtssOsdContent.Build(0, FullMetrics));
        Assert.Equal(string.Empty, RtssOsdContent.Build(4, FullMetrics));
    }
}

public sealed class RtssLhmSensorsTests
{
    private const double Mib = 1024.0 * 1024;

    [Fact]
    public void SelectsHandheldCompanionsSensors()
    {
        // LibreHardwareMonitor 0.9.7's names on the Atlas (Panther Lake, Arc B370), plus the Claw's
        // D3D memory sensors.
        RtssLhmSensors.Selection selection = new();
        selection.Add(HardwareType.Cpu, SensorType.Load, "CPU Total", 17.4);
        selection.Add(HardwareType.Cpu, SensorType.Load, "CPU Core #1", 37.5);
        selection.Add(HardwareType.Cpu, SensorType.Temperature, "Core Max", 61);
        selection.Add(HardwareType.Cpu, SensorType.Temperature, "CPU Package", 58);
        selection.Add(HardwareType.Cpu, SensorType.Power, "CPU Package", 12.6);
        selection.Add(HardwareType.Cpu, SensorType.Power, "CPU Platform", 19.6);
        selection.Add(HardwareType.GpuIntel, SensorType.Power, "GPU Power", 0.3);
        selection.Add(HardwareType.GpuIntel, SensorType.Load, "D3D 3D", 8.3);
        selection.Add(HardwareType.GpuIntel, SensorType.Load, "D3D Video Decode", 99);
        selection.Add(HardwareType.GpuIntel, SensorType.Data, "D3D Shared Memory Used", 1264.8 * Mib);
        selection.Add(HardwareType.GpuIntel, SensorType.Data, "D3D Shared Memory Total", 18409.7 * Mib);

        var metrics = selection.Build();

        Assert.Equal(17.4, metrics.CpuLoadPercent!.Value, 3);
        Assert.Equal(12.6, metrics.CpuPowerWatts!.Value, 3);
        Assert.Equal(58.0, metrics.CpuTemperatureC!.Value, 3);
        Assert.Equal(8.3, metrics.GpuLoadPercent!.Value, 3);
        Assert.Equal(0.3, metrics.GpuPowerWatts!.Value, 3);
        Assert.Null(metrics.GpuTemperatureC);
        Assert.Equal(1264.8 / 1024.0, metrics.GpuMemoryUsedGb!.Value, 3);
        Assert.Equal(18409.7 / 1024.0, metrics.GpuMemoryTotalGb!.Value, 3);
        // System memory and battery are kernel-fed.
        Assert.Null(metrics.MemoryUsedGb);
        Assert.Null(metrics.BatteryPercent);
    }

    [Fact]
    public void PrefersDedicatedGpuMemoryOverShared()
    {
        RtssLhmSensors.Selection selection = new();
        selection.Add(HardwareType.GpuAmd, SensorType.Data, "D3D Shared Memory Used", 2048 * Mib);
        selection.Add(HardwareType.GpuAmd, SensorType.Data, "GPU Memory Used", 1024 * Mib);
        selection.Add(HardwareType.GpuAmd, SensorType.Data, "GPU Memory Total", 4096 * Mib);

        var metrics = selection.Build();

        Assert.Equal(1.0, metrics.GpuMemoryUsedGb!.Value, 3);
        Assert.Equal(4.0, metrics.GpuMemoryTotalGb!.Value, 3);
    }

    [Fact]
    public void IgnoresSensorsOutsideCpuAndGpu()
    {
        RtssLhmSensors.Selection selection = new();
        selection.Add(HardwareType.Motherboard, SensorType.Temperature, "CPU Package", 90);
        selection.Add(HardwareType.Memory, SensorType.Power, "GPU Power", 5);

        Assert.Equal(RtssOsdMetrics.Empty, selection.Build());
    }
}
public sealed class RtssOsdCustomTests
{
    private static readonly RtssOsdMetrics Metrics = RtssOsdMetrics.Empty with
    {
        CpuLoadPercent = 40,
        CpuPowerWatts = 12,
        GpuLoadPercent = 30,
        GpuPowerWatts = 8,
        GpuMemoryUsedGb = 1.5,
        GpuMemoryTotalGb = 18.0,
        MemoryUsedGb = 10.0,
        MemoryTotalGb = 32.0,
        BatteryPercent = 80,
        BatteryWatts = -12,
        BatteryMinutesRemaining = 60,
        OnAcPower = false
    };

    [Fact]
    public void FromConfig_ParsesOrderAndDropsUnknownNames()
    {
        var config = new PerformanceConfig
        {
            OsdCustomOrder = "fps, cpu, Nonsense, CPU, batt",
            OsdCustomFps = 7,
            OsdCustomBattery = -3
        };

        var settings = RtssOsdCustomSettings.FromConfig(config);

        Assert.Equal(["FPS", "CPU", "BATT"], settings.Order);
        Assert.Equal(2, settings.Fps);
        Assert.Equal(0, settings.Battery);
    }

    [Fact]
    public void BuildCustom_RendersOneRowPerConfiguredWidgetInOrder()
    {
        var settings = RtssOsdCustomSettings.Default;

        var text = RtssOsdContent.BuildCustom(settings, Metrics);

        var rows = text.Split('\n');
        // Default order Time,GPU,CPU,VRAM,RAM,BATT,FPS — every widget has a source here.
        Assert.Equal(7, rows.Length);
        Assert.Contains("TIME", rows[0]);
        Assert.Contains("GPU", rows[1]);
        Assert.Contains("CPU", rows[2]);
        Assert.Contains("VRAM", rows[3]);
        Assert.Contains("RAM", rows[4]);
        Assert.Contains("BATT", rows[5]);
        Assert.Contains("FPS", rows[6]);
        Assert.Contains("<FR>", rows[6]);
    }

    [Fact]
    public void BuildCustom_PrependsPowerStatus()
    {
        var power = new RtssOsdPowerStatus(18, true, true, 17, "Lowering");

        var text = RtssOsdContent.BuildCustom(
            RtssOsdCustomSettings.Default,
            Metrics,
            power);

        var rows = text.Split('\n');
        Assert.Contains("TDP", rows[0]);
        Assert.Contains("18<S1>W", rows[0]);
        Assert.Contains("AUTO TDP", rows[1]);
        Assert.Contains("17<S1>W", rows[1]);
        Assert.Contains("LOWERING", rows[1]);
    }

    [Fact]
    public void BuildCustom_SkipsHiddenWidgetsAndSourcelessEntries()
    {
        var settings = RtssOsdCustomSettings.Default with { Gpu = 0 };
        var noBattery = Metrics with { BatteryPercent = null, BatteryWatts = null };

        var text = RtssOsdContent.BuildCustom(settings, noBattery);

        Assert.DoesNotContain("GPU", text.Replace("VRAM", string.Empty));
        Assert.DoesNotContain("BATT", text);
        Assert.Contains("CPU", text);
    }
}

public sealed class RtssOsdAutoTdpActivityTests
{
    [Theory]
    [InlineData((int)AutoTdpAction.Hold, "on-target", "Holding")]
    [InlineData((int)AutoTdpAction.Hold, "tracking-headroom", "Holding")]
    [InlineData((int)AutoTdpAction.Raise, "sustained-miss", "Raising")]
    [InlineData((int)AutoTdpAction.Probe, "probe-down", "Lowering")]
    [InlineData((int)AutoTdpAction.Restore, "probe-failed", "Restoring")]
    [InlineData((int)AutoTdpAction.Restore, "probe-interrupted", "Restoring")]
    [InlineData((int)AutoTdpAction.Hold, "settling", "Settling")]
    [InlineData((int)AutoTdpAction.Hold, "probe-pending", "Testing")]
    [InlineData((int)AutoTdpAction.Hold, "quarantine-hiatus", "Stalled")]
    [InlineData((int)AutoTdpAction.Hold, "quarantine-recovery", "Stalled")]
    [InlineData((int)AutoTdpAction.Hold, "unresponsive", "Holding")]
    public void ControllingState_DescribesTheCurrentDecision(
        int action,
        string detail,
        string expected)
    {
        var status = new AutoTdpStatus(
            AutoTdpState.Controlling,
            17,
            16.6,
            16.7,
            "steam:480",
            detail,
            (AutoTdpAction)action);

        Assert.Equal(expected, ShellSession.AutoTdpActivity(true, status));
    }

    [Fact]
    public void DisabledState_HasNoActivity()
    {
        Assert.Equal(string.Empty, ShellSession.AutoTdpActivity(false, null));
    }

    [Theory]
    [InlineData("probe-down", "Testing a lower limit")]
    [InlineData("below-learned-floor", "below-learned-floor")]
    [InlineData("Paused by a manual change.", "Paused by a manual change.")]
    public void AReasonTokenBecomesSomethingWorthShowing(string reason, string expected)
    {
        Assert.Equal(expected, AutoTdpReason.Describe(reason));
    }
}

public sealed class RtssOsdSlotsTests
{
    private const int EntrySize = 4608;
    private const int ArrayOffset = 96;
    private const int Slots = 4;
    private const int OwnerOffset = 256;
    private const int TextExOffset = 512;
    private const int FrameOffset = 32;

    [Fact]
    public void ClaimsTheFirstFreeSlotAndSkipsRtssOwnSlot()
    {
        var region = NewRegion();

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));

        Assert.Equal("WSGM", region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal("hello", region.ReadString(EntryOffset(1) + TextExOffset));
        // Slot 0 belongs to RTSS itself and stays untouched.
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(0) + OwnerOffset));
        Assert.Equal(1u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReusesItsOwnSlotAndRespectsOtherOwners()
    {
        var region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "RTSSSharedMemorySample");
        region.WriteString(EntryOffset(2) + OwnerOffset, "WSGM");

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "updated"));

        Assert.Equal("updated", region.ReadString(EntryOffset(2) + TextExOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + TextExOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(3) + TextExOffset));
    }

    [Fact]
    public void RefusesWhenEverySlotIsForeignOwned()
    {
        var region = NewRegion();
        for (var slot = 1; slot < Slots; slot++)
        {
            region.WriteString(EntryOffset(slot) + OwnerOffset, $"owner-{slot}");
        }

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
        Assert.Equal(0u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReleaseZeroesOnlyTheOwnedEntry()
    {
        var region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "someone-else");
        region.WriteString(EntryOffset(1) + TextExOffset, "theirs");
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "ours"));

        RtssOsdSlots.Release(region, "WSGM");

        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + TextExOffset));
        Assert.Equal("someone-else", region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal("theirs", region.ReadString(EntryOffset(1) + TextExOffset));
    }

    [Fact]
    public void SkipsTheTextWriteWhenTheBusyFlagIsHeld()
    {
        var region = NewRegion();
        region.BusyHeld = true;

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));

        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + TextExOffset));
        Assert.Equal(0u, region.ReadUInt32(FrameOffset));
        region.BusyHeld = false;
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
        Assert.Equal("hello", region.ReadString(EntryOffset(1) + TextExOffset));
    }

    [Fact]
    public void AFailedTextWriteAlwaysReleasesTheBusyFlag()
    {
        var region = NewRegion();
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "old"));
        region.ThrowOnWriteAt = EntryOffset(1) + TextExOffset;

        Assert.Throws<IOException>(() => RtssOsdSlots.TryWrite(region, "WSGM", "new"));

        Assert.False(region.BusyHeld);
        Assert.Equal(1u, region.ReadUInt32(FrameOffset));
        region.ThrowOnWriteAt = null;
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "new"));
    }

    [Fact]
    public void ClearingAnAbsentDisplayDoesNotClaimAFreeSlot()
    {
        var region = NewRegion();

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", string.Empty));

        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal(0u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReleaseRespectsTheBusyFlagAndClearsEveryMatchingOwner()
    {
        var region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "WSGM");
        region.WriteString(EntryOffset(2) + OwnerOffset, "WSGM");
        region.WriteString(EntryOffset(3) + OwnerOffset, "foreign");
        region.BusyHeld = true;

        RtssOsdSlots.Release(region, "WSGM");
        Assert.Equal("WSGM", region.ReadString(EntryOffset(1) + OwnerOffset));

        region.BusyHeld = false;
        RtssOsdSlots.Release(region, "WSGM");
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + OwnerOffset));
        Assert.Equal("foreign", region.ReadString(EntryOffset(3) + OwnerOffset));
        Assert.False(region.BusyHeld);
    }

    [Fact]
    public void RefusesAForeignMapping()
    {
        var region = NewRegion();
        region.WriteUInt32(0, 0x12345678);

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
    }

    private static long EntryOffset(int slot)
    {
        return ArrayOffset + (long)slot * EntrySize;
    }

    private static FakeRegion NewRegion()
    {
        var region = new FakeRegion(ArrayOffset + Slots * EntrySize);
        // 'RTSS' as the server's C multichar constant — bytes "SSTR" in memory.
        region.WriteUInt32(0, 0x52545353);
        region.WriteUInt32(4, 0x0002_0015);
        region.WriteUInt32(20, EntrySize);
        region.WriteUInt32(24, ArrayOffset);
        region.WriteUInt32(28, Slots);
        return region;
    }

    private sealed class FakeRegion(long capacity) : IRtssOsdRegion
    {
        private readonly byte[] _memory = new byte[capacity];

        public bool BusyHeld { get; set; }
        public long? ThrowOnWriteAt { get; set; }

        public long Capacity => _memory.Length;

        public uint ReadUInt32(long offset)
        {
            return BitConverter.ToUInt32(_memory, (int)offset);
        }

        public void WriteUInt32(long offset, uint value)
        {
            BitConverter.GetBytes(value).CopyTo(_memory, (int)offset);
        }

        public void ReadBytes(long offset, byte[] buffer, int count)
        {
            Array.Copy(_memory, offset, buffer, 0, count);
        }

        public void WriteBytes(long offset, byte[] buffer, int count)
        {
            if (offset == ThrowOnWriteAt)
            {
                throw new IOException("Injected slot-write failure.");
            }

            Array.Copy(buffer, 0, _memory, offset, count);
        }

        public bool TryAcquireBusy(long offset)
        {
            if (BusyHeld)
            {
                return false;
            }

            BusyHeld = true;
            return true;
        }

        public void ReleaseBusy(long offset)
        {
            BusyHeld = false;
        }

        public string ReadString(long offset)
        {
            var end = (int)offset;
            while (end < _memory.Length && _memory[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(_memory, (int)offset, end - (int)offset);
        }

        public void WriteString(long offset, string value)
        {
            Encoding.ASCII.GetBytes(value).CopyTo(_memory, (int)offset);
        }
    }
}

public sealed class RtssOsdRendererTests
{
    [Fact]
    public async Task RichOverlayPublishesBeforeItsSensorCollectorCanBlock()
    {
        using var release = new ManualResetEventSlim();
        var sampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var renderer = new RtssOsdRenderer(write: text =>
        {
            published.TrySetResult(text);
            return true;
        }, sample: () =>
        {
            sampling.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return RtssOsdMetrics.Empty;
        });
        renderer.SetLevel(2);
        renderer.Start();
        try
        {
            await sampling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(published.Task.IsCompleted);
            Assert.Contains("<FR>", await published.Task);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task MinimalOverlayPublishesWithoutCollectingSensors()
    {
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = 0;
        await using var renderer = new RtssOsdRenderer(write: text =>
        {
            if (text.Length > 0)
            {
                published.TrySetResult(text);
            }

            return true;
        }, sample: () =>
        {
            Interlocked.Increment(ref samples);
            throw new IOException("Sensors unavailable.");
        });

        renderer.SetLevel(1);
        renderer.Start();
        var text = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.DisposeAsync();

        Assert.Contains("<FR>", text);
        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task ARefusedClearIsRetriedInsteadOfParkingTheRenderer()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clears = 0;
        await using var renderer = new RtssOsdRenderer(write: text =>
        {
            if (text.Length > 0)
            {
                shown.TrySetResult();
                return true;
            }

            if (Interlocked.Increment(ref clears) == 1)
            {
                return false;
            }

            cleared.TrySetResult();
            return true;
        });

        renderer.SetLevel(1);
        renderer.Start();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        renderer.SetLevel(0);
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, clears);
    }
}
