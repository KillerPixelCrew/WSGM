using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DevicePowerPresetsTests
{
    private static DevicePowerPresetSelection Selection(DevicePowerPresets service, bool readOnly)
    {
        PerformanceConfig config = new();
        DevicePowerAssignments assignments = new(service, () => new(config, null, "fixture", 1, true, true),
            (_, ac, reference) =>
            {
                if (ac) { config.AcPowerPreset = reference; }
                else { config.BatteryPowerPreset = reference; }
                return Task.CompletedTask;
            });
        return new(service, readOnly, assignments);
    }

    private static readonly DevicePowerPreset Battery = new("battery", "Battery", 8, 9, DevicePowerMode.BetterBattery);
    private static readonly DevicePowerPreset Balanced = new("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced);
    private static readonly DevicePowerPreset Extreme = new("extreme", "Extreme", 30, 31, DevicePowerMode.BestPerformance);

    internal sealed class ModeApi : IPowerModeApi
    {
        internal Guid Mode = Guid.Empty;
        internal bool FailWrite;
        internal bool IgnoreWrite;
        internal bool FailRead;
        internal Action? AfterWrite;
        internal Action? AfterRead;
        internal int Writes;
        public Guid Read()
        {
            if (FailRead) { throw new InvalidOperationException("Windows read failed."); }
            AfterRead?.Invoke();
            return Mode;
        }
        public void Set(Guid mode)
        {
            Writes++;
            if (FailWrite) { throw new InvalidOperationException("Windows refused the mode."); }
            if (!IgnoreWrite) { Mode = mode; }
            AfterWrite?.Invoke();
        }
    }

    private static DeviceCapabilityView View(CapabilityRole role, int watts) => new(
        new CapabilityDescriptor
        {
            CapabilityId = role.ToString(),
            Role = role,
            ValueKind = CapabilityValueKind.Integer,
            Display = new() { Key = DisplayKey.SustainedPowerLimit },
            Persistence = CapabilityPersistence.Volatile,
            Unit = CapabilityUnit.Watt,
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = 8,
            Maximum = 37,
            Step = 1,
            PowerPresets = role == CapabilityRole.PowerSustainedLimit ? [Battery, Balanced, Extreme] : [],
        },
        new CapabilityProjection
        {
            State = new CapabilityState
            {
                CapabilityId = role.ToString(),
                Available = true,
                Quality = HardwareStateQuality.Verified,
                ObservedValue = new() { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
                CycleGeneration = 1,
                DescriptorGeneration = 1,
                ObservedAt = DateTimeOffset.UtcNow,
            },
        }, null);

    internal sealed class Rig
    {
        internal DeviceCapabilityView[] Views = [View(CapabilityRole.PowerSustainedLimit, 17), View(CapabilityRole.PowerSlowLimit, 18)];
        internal readonly ModeApi Api = new();
        internal readonly List<(string Id, int Watts)> Calls = [];
        internal int FailAt;
        internal bool ReplaceGeneration;
        internal bool? OnAc = true;
        internal string? LastScenario;
        internal Action<string>? AfterDeviceWrite;
        internal TaskCompletionSource? WaitForWrite;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DevicePowerPresets Create() => new(() => Views, async (id, value, cycle, generation, persist, token) =>
        {
            int watts = value.IntegerValue ?? 0;
            Assert.Equal(1, cycle);
            Assert.Equal(1, generation);
            Calls.Add((id, watts));
            Entered.TrySetResult();
            if (WaitForWrite is not null) { await WaitForWrite.Task.WaitAsync(token); }
            bool fail = FailAt == Calls.Count;
            if (!fail)
            {
                int index = Array.FindIndex(Views, view => view.Descriptor.CapabilityId == id);
                LastScenario = value.ChoiceValue ?? LastScenario;
                Views[index] = Views[index] with
                {
                    Projection = Views[index].Projection with
                    {
                        State = Views[index].Projection.State with
                        {
                            ObservedValue = value,
                            CycleGeneration = ReplaceGeneration ? 2 : 1,
                        },
                    },
                };
                AfterDeviceWrite?.Invoke(id);
            }
            return new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = fail ? CommandOutcome.Indeterminate : CommandOutcome.AppliedVerified,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }, new WindowsPowerModes(Api), () => OnAc);

        internal void AddScenarios()
        {
            Views[0] = Views[0] with
            {
                Descriptor = Views[0].Descriptor with
                {
                    PowerPresets = [Battery with { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" },
                        Balanced with { ScenarioOnAc = "green", ScenarioOnDc = "comfort" },
                        Extreme with { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" }],
                },
            };
            var scenario = View(CapabilityRole.ScenarioMode, 0);
            Views = [.. Views, scenario with
            {
                Descriptor = scenario.Descriptor with
                {
                    ValueKind = CapabilityValueKind.Choice,
                    Choices = new[] { "eco", "green", "sport", "comfort" }
                        .Select(value => new CapabilityChoice(value, new() { Key = DisplayKey.PerformanceProfile })).ToArray(),
                },
                Projection = scenario.Projection with
                {
                    State = scenario.Projection.State with
                    { ObservedValue = new() { Kind = CapabilityValueKind.Choice, ChoiceValue = "green" } },
                },
            }];
        }
    }

    [Theory]
    [InlineData(17, 18, 1, "balanced")]
    [InlineData(16, 18, 1, "custom")]
    [InlineData(17, 19, 1, "custom")]
    [InlineData(17, 18, 2, "custom")]
    [InlineData(8, 9, 0, "battery")]
    [InlineData(30, 31, 2, "extreme")]
    public void CurrentPresetComesFromEveryObservedValue(int sustained, int slow, int mode, string expected)
    {
        var state = DevicePowerPresets.Project([View(CapabilityRole.PowerSustainedLimit, sustained),
            View(CapabilityRole.PowerSlowLimit, slow)], WindowsPowerModes.Id((DevicePowerMode)mode));
        Assert.Equal(expected, state.Current);
        Assert.True(state.Available);
    }

    [Theory]
    [InlineData("battery", "PowerSustainedLimit", 8, 9)]
    [InlineData("extreme", "PowerSlowLimit", 30, 31)]
    public async Task AppliesThePairInSafeOrderThenWindowsMode(string preset, string first, int sustained, int slow)
    {
        Rig rig = new();
        DevicePowerPresets service = rig.Create();
        Assert.True((await service.ApplyAsync(preset, default)).Succeeded);
        Assert.Equal(first, rig.Calls[0].Id);
        Assert.Equal(sustained, rig.Views[0].Projection.State.ObservedValue!.IntegerValue);
        Assert.Equal(slow, rig.Views[1].Projection.State.ObservedValue!.IntegerValue);
        Assert.Equal(preset, (await service.ReadAsync()).Current);
        Assert.Equal(1, rig.Api.Writes);
    }

    [Fact]
    public async Task ExternalChangesSwitchBothSurfacesToCustomWithoutAnyWrites()
    {
        Rig rig = new();
        var service = rig.Create();
        var qam = new NativeQamPowerPresetService(service);
        using var overlay = Selection(service, false);
        await overlay.RefreshAsync();
        Assert.Equal("balanced", overlay.State.Current);
        rig.Views[0] = View(CapabilityRole.PowerSustainedLimit, 16);
        await overlay.RefreshAsync();
        Assert.Equal("custom", overlay.State.Current);
        Assert.Equal("custom", (await qam.ReadAsync())!.Current);
        rig.Views[0] = View(CapabilityRole.PowerSustainedLimit, 17);
        rig.Api.Mode = WindowsPowerModes.Id(DevicePowerMode.BestPerformance);
        Assert.Equal("custom", (await qam.ReadAsync())!.Current);
        Assert.Empty(rig.Calls);
        Assert.Equal(0, rig.Api.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UncertainDeviceWriteStopsThePresetWithoutRetryOrWindowsWrite(int failAt)
    {
        Rig rig = new() { FailAt = failAt };
        var service = rig.Create();
        var result = await service.ApplyAsync("extreme", default);
        Assert.False(result.Succeeded);
        Assert.Contains("some values may have changed", result.Error);
        Assert.Equal(failAt, rig.Calls.Count);
        Assert.Equal(0, rig.Api.Writes);
        await service.ReadAsync();
        Assert.Equal(failAt, rig.Calls.Count);
    }

    [Fact]
    public async Task WindowsFailureLeavesHonestCustomStateAndDoesNotUndoDeviceWrites()
    {
        Rig rig = new();
        rig.Api.FailWrite = true;
        var service = rig.Create();
        Assert.False((await service.ApplyAsync("extreme", default)).Succeeded);
        Assert.Equal(2, rig.Calls.Count);
        Assert.Equal("custom", (await service.ReadAsync()).Current);
        Assert.Equal(1, rig.Api.Writes);
    }

    [Fact]
    public async Task NewDeviceGenerationStopsBeforeAnotherWrite()
    {
        Rig rig = new() { ReplaceGeneration = true };
        Assert.False((await rig.Create().ApplyAsync("battery", default)).Succeeded);
        Assert.Single(rig.Calls);
        Assert.Equal(0, rig.Api.Writes);
    }

    [Fact]
    public async Task MissingStaleAndAmbiguousCapabilitiesDisableSelection()
    {
        Rig rig = new();
        var stale = rig.Views[0] with
        {
            Projection = rig.Views[0].Projection with
            { State = rig.Views[0].Projection.State with { Quality = HardwareStateQuality.Stale } }
        };
        Assert.False(DevicePowerPresets.Project([stale, rig.Views[1]], Guid.Empty).Available);
        Assert.False(DevicePowerPresets.Project([.. rig.Views, rig.Views[0]], Guid.Empty).Available);
        rig.Views = [];
        Assert.Empty((await rig.Create().ReadAsync()).Presets);
        Assert.False((await rig.Create().ApplyAsync("battery", default)).Succeeded);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task CustomUnknownAndPreviewSelectionsNeverWrite()
    {
        Rig rig = new();
        var service = rig.Create();
        Assert.False((await service.ApplyAsync("custom", default)).Succeeded);
        Assert.False((await service.ApplyAsync("unknown", default)).Succeeded);
        using var overlay = Selection(service, true);
        await overlay.RefreshAsync();
        await overlay.AssignAsync(true, "battery");
        Assert.Empty(rig.Calls);
    }

    [Theory]
    [InlineData(DevicePowerMode.BetterBattery, "961cc777-2547-4f9d-8174-7d86181b8a7a")]
    [InlineData(DevicePowerMode.Balanced, "00000000-0000-0000-0000-000000000000")]
    [InlineData(DevicePowerMode.BestPerformance, "ded574b5-45a0-4f42-8737-46345c09c238")]
    public void WindowsModesUseHCsOverlayGuids(DevicePowerMode mode, string expected) =>
        Assert.Equal(Guid.Parse(expected), WindowsPowerModes.Id(mode));

    [Fact]
    public async Task WindowsReadFailureDisablesBothSurfacesAndPreventsDeviceWrites()
    {
        Rig rig = new();
        rig.Api.FailRead = true;
        var service = rig.Create();
        Assert.False((await service.ReadAsync()).Available);
        Assert.False((await new NativeQamPowerPresetService(service).ReadAsync())!.Available);
        Assert.False((await service.ApplyAsync("battery", default)).Succeeded);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task UnconfirmedWindowsModeIsNotReportedAsSuccess()
    {
        Rig rig = new();
        rig.Api.IgnoreWrite = true;
        var service = rig.Create();
        Assert.False((await service.ApplyAsync("battery", default)).Succeeded);
        Assert.Equal("custom", (await service.ReadAsync()).Current);
        Assert.Equal(1, rig.Api.Writes);
    }

    [Fact]
    public async Task DriftDuringFinalConfirmationReportsCustomWithoutFightingTheChange()
    {
        Rig rig = new();
        rig.Api.AfterWrite = () => rig.Views[0] = View(CapabilityRole.PowerSustainedLimit, 20);
        var service = rig.Create();
        Assert.False((await service.ApplyAsync("extreme", default)).Succeeded);
        Assert.Equal("custom", (await service.ReadAsync()).Current);
        Assert.Equal(2, rig.Calls.Count);
        Assert.Equal(1, rig.Api.Writes);
    }

    [Fact]
    public async Task WithdrawalRemovesThePresetChoicesFromBothSurfaces()
    {
        Rig rig = new();
        var service = rig.Create();
        using var overlay = Selection(service, false);
        await overlay.RefreshAsync();
        Assert.NotEmpty(overlay.State.Presets);
        rig.Views = [];
        await overlay.RefreshAsync();
        Assert.Empty(overlay.State.Presets);
        Assert.Empty((await new NativeQamPowerPresetService(service).ReadAsync())!.Options);
        await overlay.AssignAsync(true, "battery");
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task ClosingOverlayCancelsRemainingWritesAndSuppressesLatePublication()
    {
        Rig rig = new() { WaitForWrite = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var overlay = Selection(rig.Create(), false);
        await overlay.RefreshAsync();
        Task apply = overlay.AssignAsync(true, "battery");
        await rig.Entered.Task;
        overlay.Dispose();
        bool changed = false;
        overlay.Changed += () => changed = true;
        await apply;
        Assert.False(changed);
        Assert.Single(rig.Calls);
        Assert.Equal(0, rig.Api.Writes);
    }

    [Fact]
    public async Task SelectionDuringBackgroundReadIsAcceptedAndWinsOverOldObservation()
    {
        Rig rig = new();
        using var overlay = Selection(rig.Create(), false);
        await overlay.RefreshAsync();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Api.AfterRead = () =>
        {
            rig.Api.AfterRead = null;
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        Task refresh = overlay.RefreshAsync();
        await entered.Task;
        Task apply;
        try
        {
            Assert.True(overlay.CanAssign);
            apply = overlay.AssignAsync(true, "battery");
            Assert.True(overlay.Busy);
        }
        finally { release.Set(); }
        await Task.WhenAll(refresh, apply);
        Assert.Equal("battery", overlay.State.Current);
        Assert.Equal(2, rig.Calls.Count);
    }

    [Fact]
    public async Task CancellationAfterPreflightPropagatesWithoutReportingAnUnstartedMutation()
    {
        Rig rig = new();
        using CancellationTokenSource cancellation = new();
        rig.Api.AfterRead = cancellation.Cancel;
        var service = rig.Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyAsync("battery", cancellation.Token));
        Assert.Empty(rig.Calls);
        Assert.Equal(0, rig.Api.Writes);
        rig.Api.AfterRead = null;
        Assert.DoesNotContain("some values", (await service.ReadAsync()).Status);
    }

    [Theory]
    [InlineData("battery", true, "eco")]
    [InlineData("balanced", true, "green")]
    [InlineData("extreme", true, "sport")]
    [InlineData("battery", false, "comfort")]
    [InlineData("balanced", false, "comfort")]
    [InlineData("extreme", false, "comfort")]
    public async Task PresetsApplyTheAuthoredScenarioForTheCurrentPowerSource(string preset, bool ac, string expected)
    {
        Rig rig = new() { OnAc = ac };
        rig.AddScenarios();
        var service = rig.Create();
        Assert.True((await service.ApplyAsync(preset, default)).Succeeded);
        Assert.Equal("ScenarioMode", rig.Calls[0].Id);
        Assert.Equal(expected, rig.LastScenario);
        Assert.Equal(3, rig.Calls.Count);
        Assert.Equal(preset, (await service.ReadAsync()).Current);
    }

    [Fact]
    public async Task ScenarioResetOfPowerLimitsChangesTheRequiredWriteOrder()
    {
        Rig rig = new();
        rig.AddScenarios();
        rig.AfterDeviceWrite = id =>
        {
            if (id != "ScenarioMode") { return; }
            for (int i = 0; i < 2; i++)
            {
                rig.Views[i] = rig.Views[i] with
                {
                    Projection = rig.Views[i].Projection with
                    {
                        State = rig.Views[i].Projection.State with
                        { ObservedValue = new() { Kind = CapabilityValueKind.Integer, IntegerValue = 8 } }
                    }
                };
            }
        };
        Assert.True((await rig.Create().ApplyAsync("balanced", default)).Succeeded);
        Assert.Equal("PowerSlowLimit", rig.Calls[1].Id);
    }

    [Fact]
    public async Task ScenarioFailureStopsBeforeWattsAndWindowsWithoutRetry()
    {
        Rig rig = new() { FailAt = 1 };
        rig.AddScenarios();
        var service = rig.Create();
        Assert.False((await service.ApplyAsync("extreme", default)).Succeeded);
        await service.ReadAsync();
        Assert.Single(rig.Calls);
        Assert.Equal(0, rig.Api.Writes);
    }

    [Fact]
    public async Task UnknownPowerSourcePreventsScenarioPresetWrites()
    {
        Rig rig = new() { OnAc = null };
        rig.AddScenarios();
        var service = rig.Create();
        Assert.False((await service.ReadAsync()).Available);
        Assert.False((await service.ApplyAsync("extreme", default)).Succeeded);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task PowerSourceChangeDuringScenarioWriteStopsRemainingSteps()
    {
        Rig rig = new();
        rig.AddScenarios();
        rig.AfterDeviceWrite = _ => rig.OnAc = false;
        Assert.False((await rig.Create().ApplyAsync("extreme", default)).Succeeded);
        Assert.Single(rig.Calls);
        Assert.Equal(0, rig.Api.Writes);
    }

    [Fact]
    public async Task ScenarioDriftShowsCustomWithoutReapplying()
    {
        Rig rig = new();
        rig.AddScenarios();
        var service = rig.Create();
        Assert.Equal("balanced", (await service.ReadAsync()).Current);
        rig.OnAc = false;
        Assert.Equal("custom", (await service.ReadAsync()).Current);
        Assert.Empty(rig.Calls);
        rig.Views[2] = rig.Views[2] with
        {
            Projection = rig.Views[2].Projection with
            { State = rig.Views[2].Projection.State with { Quality = HardwareStateQuality.Stale } }
        };
        Assert.False((await service.ReadAsync()).Available);
        Assert.False((await service.ApplyAsync("battery", default)).Succeeded);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public void ProductionValidationAcceptsScenarioPresetsAndRejectsWithdrawnTargets()
    {
        Rig rig = new();
        rig.AddScenarios();
        CapabilityDescriptorSet set = new()
        {
            Generation = 1,
            CycleGeneration = 1,
            Descriptors = rig.Views.Select(view => view.Descriptor).ToArray(),
        };
        Assert.True(DeviceCapabilityValidation.TryValidateDescriptorSet(set, 1, 0, out string? error), error);
        Assert.False(DeviceCapabilityValidation.TryValidateDescriptorSet(set with
        { Descriptors = set.Descriptors.Take(2).ToArray() }, 1, 0, out _));
    }
}
