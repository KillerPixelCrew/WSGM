using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DevicePowerAssignmentsTests
{
    private static DevicePowerPresetReference Reference(string preset) => new() { PluginId = "fixture", PresetId = preset };

    private sealed class Rig
    {
        internal readonly DevicePowerPresetsTests.Rig Device = new();
        internal PerformanceConfig Config = new() { AcPowerPreset = Reference("extreme"), BatteryPowerPreset = Reference("battery") };
        internal string? Application;
        internal bool Enabled = true;
        internal string Plugin = "fixture";
        internal int Saves;
        internal long Cycle = 1;
        internal DevicePowerAssignments Create() => new(Device.Create(),
            () => new(Config, Application, Plugin, Cycle, Enabled, Device.OnAc),
            (context, ac, reference) =>
            {
                Saves++;
                var application = DevicePowerAssignments.Application(context);
                if (application is not null)
                {
                    if (ac) { application.AcPowerPreset = reference; }
                    else { application.BatteryPowerPreset = reference; }
                }
                else if (ac) { Config.AcPowerPreset = reference; }
                else { Config.BatteryPowerPreset = reference; }
                return Task.CompletedTask;
            });
    }

    [Theory]
    [InlineData(true, "pl1")]
    [InlineData(false, "pl1")]
    [InlineData(true, "pl2")]
    [InlineData(false, "pl2")]
    [InlineData(true, "mode")]
    [InlineData(false, "mode")]
    [InlineData(true, "scenario")]
    [InlineData(false, "scenario")]
    public async Task DriftSavesOnlyTheActiveSourceAndRestoresItsCompleteCustomProfile(bool ac, string change)
    {
        Rig rig = new();
        rig.Device.OnAc = ac;
        rig.Device.AddScenarios();
        var presets = rig.Device.Create();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        switch (change)
        {
            case "pl1":
                ChangeValue(rig, 0, new() { Kind = CapabilityValueKind.Integer, IntegerValue = ac ? 29 : 9 });
                break;
            case "pl2": ChangeValue(rig, 1, new() { Kind = CapabilityValueKind.Integer, IntegerValue = ac ? 32 : 10 }); break;
            case "mode": rig.Device.Api.Mode = WindowsPowerModes.Id(DevicePowerMode.Balanced); break;
            case "scenario": ChangeValue(rig, 2, new() { Kind = CapabilityValueKind.Choice, ChoiceValue = "green" }); break;
        }
        var expected = (await presets.ReadAsync()).Values;
        int writes = rig.Device.Calls.Count;
        await assignments.ReconcileAsync(default);
        var saved = ac ? rig.Config.AcPowerPreset : rig.Config.BatteryPowerPreset;
        Assert.Equal("custom", saved!.PresetId);
        Assert.Equal(expected, saved.CustomValues);
        Assert.Equal(ac ? "battery" : "extreme", (ac ? rig.Config.BatteryPowerPreset : rig.Config.AcPowerPreset)!.PresetId);
        Assert.Equal(writes, rig.Device.Calls.Count);
        await assignments.ReconcileAsync(default);
        Assert.Equal(1, rig.Saves);
        var qam = (await new NativeQamPowerPresetService(presets, assignments).ReadAsync())!;
        Assert.Equal("custom", ac ? qam.Ac : qam.Battery);
        Assert.Contains(qam.Options, item => item.Id == "custom" && item.Label == "Custom");

        rig.Device.OnAc = !ac;
        await assignments.ReconcileAsync(default);
        rig.Device.OnAc = ac;
        await assignments.ReconcileAsync(default);
        Assert.Equal(expected, (await presets.ReadAsync()).Values);
        Assert.Equal("custom", (ac ? assignments.Snapshot().AcPreset : assignments.Snapshot().BatteryPreset));
        Assert.Equal(1, rig.Saves);
    }

    private static void ChangeValue(Rig rig, int index, CapabilityValue value)
    {
        var view = rig.Device.Views[index];
        rig.Device.Views[index] = view with
        { Projection = view.Projection with { State = view.Projection.State with { ObservedValue = value } } };
    }

    [Fact]
    public async Task CustomInheritedFromGlobalBecomesALocalOverrideWithoutChangingGlobalOrOtherGames()
    {
        Rig rig = new() { Application = "steam:42" };
        rig.Config.Applications.Add(new() { ApplicationId = rig.Application, UsePerGameProfile = true });
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        ChangeValue(rig, 0, new() { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        await assignments.ReconcileAsync(default);
        Assert.Equal("custom", rig.Config.Applications[0].AcPowerPreset!.PresetId);
        Assert.Equal("extreme", rig.Config.AcPowerPreset!.PresetId);
        Assert.Null(rig.Config.Applications[0].BatteryPowerPreset);
        rig.Application = null;
        await assignments.ReconcileAsync(default);
        Assert.Equal(30, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        rig.Application = "steam:42";
        await assignments.ReconcileAsync(default);
        Assert.Equal(29, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
    }

    [Fact]
    public async Task CustomSurvivesSerializationAndSubsequentEditsWithoutPollingWritesToHardware()
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        ChangeValue(rig, 0, new() { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        await assignments.ReconcileAsync(default);
        ChangeValue(rig, 1, new() { Kind = CapabilityValueKind.Integer, IntegerValue = 32 });
        await assignments.ReconcileAsync(default);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal(2, rig.Saves);
        string json = JsonSerializer.Serialize(rig.Config, ConfigJsonContext.Default.PerformanceConfig);
        rig.Config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.PerformanceConfig)!;
        var restored = rig.Create();
        await restored.ReconcileAsync(default);
        Assert.Equal(29, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        Assert.Equal(32, rig.Device.Views[1].Projection.State.ObservedValue!.IntegerValue);
        await restored.AssignAsync(true, "balanced", default);
        Assert.Null(rig.Config.AcPowerPreset!.CustomValues);
        Assert.Equal("balanced", rig.Config.AcPowerPreset.PresetId);
    }

    [Fact]
    public async Task FailedAssignmentNeverSavesPartialReadingsAsCustom()
    {
        Rig rig = new();
        rig.Device.FailAt = 2;
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        await assignments.ReconcileAsync(default);
        Assert.Equal(0, rig.Saves);
        Assert.Equal("extreme", rig.Config.AcPowerPreset!.PresetId);
    }

    [Fact]
    public async Task EditingInactiveAssignmentDoesNotReplayActivePresetOverManualChanges()
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        ChangeValue(rig, 0, new() { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        await assignments.AssignAsync(false, "balanced", default);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal("custom", rig.Config.AcPowerPreset!.PresetId);
        Assert.Equal(29, rig.Config.AcPowerPreset.CustomValues!.SustainedWatts);
        Assert.Equal("balanced", rig.Config.BatteryPowerPreset!.PresetId);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("source")]
    [InlineData("application")]
    public async Task UnreliableOrReassignedObservationsNeverReplaceTheSavedProfile(string change)
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        ChangeValue(rig, 0, new() { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        if (change == "stale")
        {
            var view = rig.Device.Views[0];
            rig.Device.Views[0] = view with
            { Projection = view.Projection with { State = view.Projection.State with { Quality = HardwareStateQuality.Stale } } };
        }
        else { rig.Device.Api.AfterRead = () => { if (change == "source") { rig.Device.OnAc = false; } else { rig.Application = "steam:42"; } }; }
        await assignments.ReconcileAsync(default);
        Assert.Equal(0, rig.Saves);
        Assert.Equal("extreme", rig.Config.AcPowerPreset!.PresetId);
    }

    [Fact]
    public async Task SavedCustomOutsideCurrentDeviceLimitsIsRejectedWithoutWritesOrPollingRetries()
    {
        Rig rig = new();
        rig.Config.AcPowerPreset = Reference("custom") with
        { CustomValues = new() { SustainedWatts = 38, SlowWatts = 38, WindowsMode = DevicePowerMode.Balanced } };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        await assignments.ReconcileAsync(default);
        Assert.Empty(rig.Device.Calls);
        Assert.NotEmpty(assignments.Snapshot().Status);
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public void NormalizationRejectsIncompleteCustomAndClearsValuesFromNamedPresets()
    {
        AppConfig config = new();
        config.Performance.AcPowerPreset = Reference("custom");
        config.Performance.BatteryPowerPreset = Reference("balanced") with
        { CustomValues = new() { SustainedWatts = 17, SlowWatts = 18 } };
        ConfigStore.Normalize(config);
        Assert.Null(config.Performance.AcPowerPreset);
        Assert.Null(config.Performance.BatteryPowerPreset!.CustomValues);
        config.Performance.AcPowerPreset = Reference("custom") with
        { CustomValues = new() { SustainedWatts = 18, SlowWatts = 17 } };
        ConfigStore.Normalize(config);
        Assert.Null(config.Performance.AcPowerPreset);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("plugin")]
    [InlineData("cycle")]
    [InlineData("enabled")]
    [InlineData("source")]
    public async Task ScopeChangesDuringReadRejectAssignmentBeforeSaving(string change)
    {
        Rig rig = new();
        rig.Device.Api.AfterRead = () =>
        {
            switch (change)
            {
                case "application": rig.Application = "steam:42"; break;
                case "plugin": rig.Plugin = "replacement"; break;
                case "cycle": rig.Cycle++; break;
                case "enabled": rig.Enabled = false; break;
                case "source": rig.Device.OnAc = false; break;
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Create().AssignAsync(true, "balanced", default));
        Assert.Equal(0, rig.Saves);
        Assert.Empty(rig.Device.Calls);
        Assert.Equal("extreme", rig.Config.AcPowerPreset!.PresetId);
    }

    [Fact]
    public async Task ScopeFlagDrivesQamInheritanceLabels()
    {
        Rig rig = new();
        var assignments = rig.Create();
        var qam = new NativeQamPowerPresetService(rig.Device.Create(), assignments);
        Assert.True(assignments.Snapshot().IsGlobal);
        Assert.Equal("Manual selection", (await qam.ReadAsync())!.UnsetLabel);
        rig.Application = "steam:42";
        rig.Config.Applications.Add(new() { ApplicationId = rig.Application, UsePerGameProfile = true });
        Assert.False(assignments.Snapshot().IsGlobal);
        Assert.Equal("Use global assignment", (await qam.ReadAsync())!.UnsetLabel);
    }

    [Theory]
    [InlineData(" fixture ", " balanced ", true)]
    [InlineData(" ", "balanced", false)]
    [InlineData("fixture", " ", false)]
    public void SavedIdentifiersAreTrimmedBeforeValidation(string plugin, string preset, bool valid)
    {
        AppConfig config = new();
        config.Performance.AcPowerPreset = new() { PluginId = plugin, PresetId = preset };
        ConfigStore.Normalize(config);
        if (!valid) { Assert.Null(config.Performance.AcPowerPreset); return; }
        Assert.Equal("fixture", config.Performance.AcPowerPreset!.PluginId);
        Assert.Equal("balanced", config.Performance.AcPowerPreset.PresetId);
    }

    [Theory]
    [InlineData(128, 64, true)]
    [InlineData(129, 64, false)]
    [InlineData(128, 65, false)]
    public void AssignmentLengthLimitsApplyAfterTrimming(int pluginLength, int presetLength, bool valid)
    {
        AppConfig config = new();
        config.Performance.Applications.Add(new()
        {
            ApplicationId = "steam:42",
            UsePerGameProfile = true,
            BatteryPowerPreset = new() { PluginId = " " + new string('p', pluginLength) + " ", PresetId = " " + new string('b', presetLength) + " " },
        });
        ConfigStore.Normalize(config);
        var reference = Assert.Single(config.Performance.Applications).BatteryPowerPreset;
        Assert.Equal(valid, reference is not null);
        if (valid)
        {
            Assert.Equal(pluginLength, reference!.PluginId.Length);
            Assert.Equal(presetLength, reference.PresetId.Length);
        }
    }

    [Fact]
    public async Task QamAssignmentsShareTheDevicePagePolicyAndClearLocalOverrides()
    {
        Rig rig = new();
        rig.Config.AcPowerPreset = null;
        var assignments = rig.Create();
        var qam = new NativeQamPowerPresetService(rig.Device.Create(), assignments);
        Assert.True((await qam.SetAssignmentAsync(false, "balanced", default)).Succeeded);
        Assert.Equal("balanced", rig.Config.BatteryPowerPreset?.PresetId);
        Assert.Empty(rig.Device.Calls);
        var state = (await qam.ReadAsync())!;
        Assert.Equal("", state.Ac);
        Assert.Equal("balanced", state.Battery);
        Assert.DoesNotContain(state.Options, option => option.Id == "custom");
        Assert.True((await qam.SetAssignmentAsync(true, "extreme", default)).Succeeded);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.True((await qam.SetAssignmentAsync(true, null, default)).Succeeded);
        Assert.Null(rig.Config.AcPowerPreset);
        Assert.False((await qam.SetAssignmentAsync(false, "missing", default)).Succeeded);
    }

    [Fact]
    public async Task ReplacedPerGameConfigurationCannotSaveIntoTheGlobalFallback()
    {
        Rig rig = new() { Application = "steam:42" };
        rig.Config.Applications.Add(new() { ApplicationId = "steam:42", UsePerGameProfile = true });
        rig.Device.Api.AfterRead = () => rig.Config = new() { AcPowerPreset = Reference("extreme") };
        var assignments = rig.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => assignments.AssignAsync(true, "balanced", default));
        Assert.Equal(0, rig.Saves);
        Assert.Equal("extreme", rig.Config.AcPowerPreset?.PresetId);
        Assert.Empty(rig.Device.Calls);
    }

    [Fact]
    public async Task FailedSecondWriteIsNotRetriedByPolling()
    {
        Rig rig = new();
        rig.Device.FailAt = 2;
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        Assert.Equal(31, rig.Device.Views[1].Projection.State.ObservedValue!.IntegerValue);
        await assignments.ReconcileAsync(default);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal(0, rig.Device.Api.Writes);
    }

    [Fact]
    public async Task CancelledPartialAssignmentIsNotRetriedByPolling()
    {
        Rig rig = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource laterWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Device.OnWriteEntered = count => { if (count == 2) { entered.TrySetResult(); } };
        rig.Device.AfterDeviceWrite = _ =>
        {
            rig.Device.WaitForWrite = laterWrite;
        };
        var assignments = rig.Create();
        Task applying = assignments.ReconcileAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal(31, rig.Device.Views[1].Projection.State.ObservedValue!.IntegerValue);
        await assignments.ReconcileAsync(default);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal(0, rig.Device.Api.Writes);
    }

    [Fact]
    public async Task SourceTransitionsApplyOnceAndDoNotFightManualDrift()
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        Assert.Equal(2, rig.Device.Calls.Count);
        await assignments.ReconcileAsync(default);
        Assert.Equal(2, rig.Device.Calls.Count);
        rig.Device.OnAc = false;
        await assignments.ReconcileAsync(default);
        Assert.Equal(4, rig.Device.Calls.Count);
        Assert.Equal(8, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public async Task FailureIsNotRetriedUntilAnExplicitAssignment()
    {
        Rig rig = new();
        rig.Device.FailAt = 1;
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        await assignments.ReconcileAsync(default);
        Assert.Single(rig.Device.Calls);
        Assert.NotEmpty(assignments.Snapshot().Status);
        rig.Device.FailAt = 0;
        await assignments.AssignAsync(true, "extreme", default);
        Assert.Equal(3, rig.Device.Calls.Count);
        Assert.Equal(1, rig.Saves);
    }

    [Fact]
    public async Task PerGameAssignmentsOverrideAndInheritIndependently()
    {
        Rig rig = new() { Application = "steam:42" };
        rig.Config.Applications.Add(new() { ApplicationId = "steam:42", UsePerGameProfile = true, AcPowerPreset = Reference("balanced") });
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        Assert.Equal(17, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        rig.Device.OnAc = false;
        await assignments.ReconcileAsync(default);
        Assert.Equal(8, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        await assignments.AssignAsync(false, "balanced", default);
        Assert.Equal("balanced", rig.Config.Applications[0].BatteryPowerPreset!.PresetId);
        Assert.Equal("battery", rig.Config.BatteryPowerPreset!.PresetId);
        rig.Application = null;
        await assignments.ReconcileAsync(default);
        Assert.Equal(8, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
    }

    [Fact]
    public async Task DisabledIntegrationUnknownSourceAndOtherPluginNeverWrite()
    {
        Rig rig = new() { Enabled = false };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(default);
        rig.Enabled = true;
        rig.Device.OnAc = null;
        await assignments.ReconcileAsync(default);
        rig.Device.OnAc = true;
        rig.Plugin = "replacement";
        await assignments.ReconcileAsync(default);
        Assert.Empty(rig.Device.Calls);
        Assert.Contains("another device", assignments.Snapshot().Status);
    }

    [Fact]
    public void SavedAssignmentsSurviveJsonAndRtssPolicyMerges()
    {
        Rig rig = new();
        rig.Config.Applications.Add(new() { ApplicationId = "steam:42", UsePerGameProfile = true, AcPowerPreset = Reference("balanced") });
        string json = JsonSerializer.Serialize(rig.Config, ConfigJsonContext.Default.PerformanceConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.PerformanceConfig)!;
        ShellSession.MergePerformancePolicy(restored, new PerformancePolicy(new PerformanceValues(60, 1), [], true));
        Assert.Equal("extreme", restored.AcPowerPreset!.PresetId);
        Assert.Equal("balanced", Assert.Single(restored.Applications).AcPowerPreset!.PresetId);
    }
}
