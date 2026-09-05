using System.Text.Json;
using WSGM.Core;
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
        internal DevicePowerAssignments Create() => new(Device.Create(),
            () => new(Config, Application, Plugin, 1, Enabled, Device.OnAc),
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
