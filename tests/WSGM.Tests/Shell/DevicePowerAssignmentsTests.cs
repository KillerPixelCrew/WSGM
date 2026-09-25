using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DevicePowerAssignmentsTests
{
    private static DevicePowerPresetReference Reference(string preset)
    {
        return new DevicePowerPresetReference { PluginId = "fixture", PresetId = preset };
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
        Rig rig = new() { Device = { OnAc = ac } };
        rig.Device.AddScenarios();
        var presets = rig.Device.Create();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        switch (change)
        {
            case "pl1":
                ChangeValue(rig, 0,
                    new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = ac ? 29 : 9 });
                break;
            case "pl2":
                ChangeValue(rig, 1,
                    new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = ac ? 32 : 10 }); break;
            case "mode": rig.Device.Api.Mode = WindowsPowerModes.Id(DevicePowerMode.Balanced); break;
            case "scenario":
                ChangeValue(rig, 2,
                    new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "green" }); break;
        }

        var expected = (await presets.ReadAsync()).Values;
        var writes = rig.Device.Calls.Count;
        await assignments.ReconcileAsync(CancellationToken.None);
        var saved = ac ? rig.Config.Global.AcPowerPreset : rig.Config.Global.BatteryPowerPreset;
        Assert.Equal("custom", saved!.PresetId);
        Assert.Equal(expected, saved.CustomValues);
        Assert.Equal(ac ? "battery" : "extreme",
            (ac ? rig.Config.Global.BatteryPowerPreset : rig.Config.Global.AcPowerPreset)!.PresetId);
        Assert.Equal(writes, rig.Device.Calls.Count);
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(1, rig.Saves);
        var qam = (await new NativeQamPowerPresetService(presets, assignments).ReadAsync())!;
        Assert.Equal("custom", ac ? qam.Ac : qam.Battery);
        Assert.Contains(qam.Options, item => item is { Id: "custom", Label: "Custom" });

        rig.Device.OnAc = !ac;
        await assignments.ReconcileAsync(CancellationToken.None);
        rig.Device.OnAc = ac;
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(expected, (await presets.ReadAsync()).Values);
        Assert.Equal("custom", ac ? assignments.Snapshot().AcPreset : assignments.Snapshot().BatteryPreset);
        Assert.Equal(1, rig.Saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutoTdpShowsCustomForTheSourceInUseAndNeverSavesItsLimits(bool ac)
    {
        Rig rig = new() { Device = { OnAc = ac } };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);

        rig.AutoTdpOwnsPower = true;
        ChangeValue(rig, 0, new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = ac ? 29 : 9 });
        await assignments.ReconcileAsync(CancellationToken.None);

        var shown = assignments.Snapshot();
        Assert.Equal("custom", ac ? shown.AcPreset : shown.BatteryPreset);
        Assert.Equal(ac ? "battery" : "extreme", ac ? shown.BatteryPreset : shown.AcPreset);
        Assert.Equal(0, rig.Saves);
        Assert.Equal("extreme", rig.Config.Global.AcPowerPreset!.PresetId);
        Assert.Equal("battery", rig.Config.Global.BatteryPowerPreset!.PresetId);

        rig.AutoTdpOwnsPower = false;
        shown = assignments.Snapshot();
        Assert.Equal(ac ? "extreme" : "battery", ac ? shown.AcPreset : shown.BatteryPreset);
    }

    private static void ChangeValue(Rig rig, int index, CapabilityValue value)
    {
        var view = rig.Device.Views[index];
        rig.Device.Views[index] = view with
        {
            Projection = view.Projection with { State = view.Projection.State with { ObservedValue = value } }
        };
    }

    [Fact]
    public async Task CustomInheritedFromGlobalBecomesALocalOverrideWithoutChangingGlobalOrOtherGames()
    {
        Rig rig = new() { Application = "steam:42" };
        rig.Config.Games.Add(new GameProfile
            { Id = rig.Application, Enabled = true });
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        ChangeValue(rig, 0, new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal("custom", rig.Config.Games[0].Values.AcPowerPreset!.PresetId);
        Assert.Equal("extreme", rig.Config.Global.AcPowerPreset!.PresetId);
        Assert.Null(rig.Config.Games[0].Values.BatteryPowerPreset);
        rig.Application = null;
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(30, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        rig.Application = "steam:42";
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(29, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
    }

    [Fact]
    public async Task CustomSurvivesSerializationAndSubsequentEditsWithoutPollingWritesToHardware()
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        ChangeValue(rig, 0, new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        await assignments.ReconcileAsync(CancellationToken.None);
        ChangeValue(rig, 1, new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 32 });
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal(2, rig.Saves);
        var json = JsonSerializer.Serialize(rig.Config, ConfigJsonContext.Default.ProfileConfig);
        rig.Config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ProfileConfig)!;
        var restored = rig.Create();
        await restored.ReconcileAsync(CancellationToken.None);
        Assert.Equal(29, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        Assert.Equal(32, rig.Device.Views[1].Projection.State.ObservedValue!.IntegerValue);
        await restored.AssignAsync(true, "balanced", CancellationToken.None);
        Assert.Null(rig.Config.Global.AcPowerPreset!.CustomValues);
        Assert.Equal("balanced", rig.Config.Global.AcPowerPreset.PresetId);
    }

    [Fact]
    public async Task FailedAssignmentNeverSavesPartialReadingsAsCustom()
    {
        Rig rig = new() { Device = { FailAt = 2 } };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(0, rig.Saves);
        Assert.Equal("extreme", rig.Config.Global.AcPowerPreset!.PresetId);
    }

    [Fact]
    public async Task EditingInactiveAssignmentDoesNotReplayActivePresetOverManualChanges()
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        ChangeValue(rig, 0, new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        await assignments.AssignAsync(false, "balanced", CancellationToken.None);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal("custom", rig.Config.Global.AcPowerPreset!.PresetId);
        Assert.Equal(29, rig.Config.Global.AcPowerPreset.CustomValues!.SustainedWatts);
        Assert.Equal("balanced", rig.Config.Global.BatteryPowerPreset!.PresetId);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("source")]
    [InlineData("application")]
    public async Task UnreliableOrReassignedObservationsNeverReplaceTheSavedProfile(string change)
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        ChangeValue(rig, 0, new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 29 });
        if (change == "stale")
        {
            var view = rig.Device.Views[0];
            rig.Device.Views[0] = view with
            {
                Projection = view.Projection with
                {
                    State = view.Projection.State with { Quality = HardwareStateQuality.Stale }
                }
            };
        }
        else
        {
            rig.Device.Api.AfterRead = () =>
            {
                if (change == "source")
                {
                    rig.Device.OnAc = false;
                }
                else
                {
                    rig.Application = "steam:42";
                }
            };
        }

        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(0, rig.Saves);
        Assert.Equal("extreme", rig.Config.Global.AcPowerPreset!.PresetId);
    }

    [Fact]
    public async Task SavedCustomOutsideCurrentDeviceLimitsIsRejectedWithoutWritesOrPollingRetries()
    {
        Rig rig = new()
        {
            Config =
            {
                Global =
                {
                    AcPowerPreset = Reference("custom") with
                    {
                        CustomValues = new DevicePowerCustomValues
                            { SustainedWatts = 38, SlowWatts = 38, WindowsMode = DevicePowerMode.Balanced }
                    }
                }
            }
        };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Empty(rig.Device.Calls);
        Assert.NotEmpty(assignments.Snapshot().Status);
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public void NormalizationRejectsIncompleteCustomAndClearsValuesFromNamedPresets()
    {
        AppConfig config = new()
        {
            Profiles =
            {
                Global =
                {
                    AcPowerPreset = Reference("custom"),
                    BatteryPowerPreset = Reference("balanced") with
                    {
                        CustomValues = new DevicePowerCustomValues { SustainedWatts = 17, SlowWatts = 18 }
                    }
                }
            }
        };
        ConfigStore.Normalize(config);
        Assert.Null(config.Profiles.Global.AcPowerPreset);
        Assert.Null(config.Profiles.Global.BatteryPowerPreset!.CustomValues);
        config.Profiles.Global.AcPowerPreset = Reference("custom") with
        {
            CustomValues = new DevicePowerCustomValues { SustainedWatts = 18, SlowWatts = 17 }
        };
        ConfigStore.Normalize(config);
        Assert.Null(config.Profiles.Global.AcPowerPreset);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Create().AssignAsync(true, "balanced", CancellationToken.None));
        Assert.Equal(0, rig.Saves);
        Assert.Empty(rig.Device.Calls);
        Assert.Equal("extreme", rig.Config.Global.AcPowerPreset!.PresetId);
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
        rig.Config.Games.Add(new GameProfile
            { Id = rig.Application, Enabled = true });
        Assert.False(assignments.Snapshot().IsGlobal);
        Assert.Equal("Use global assignment", (await qam.ReadAsync())!.UnsetLabel);
    }

    [Theory]
    [InlineData(" fixture ", " balanced ", true)]
    [InlineData(" ", "balanced", false)]
    [InlineData("fixture", " ", false)]
    public void SavedIdentifiersAreTrimmedBeforeValidation(string plugin, string preset, bool valid)
    {
        AppConfig config = new()
        {
            Profiles =
            {
                Global = { AcPowerPreset = new DevicePowerPresetReference { PluginId = plugin, PresetId = preset } }
            }
        };
        ConfigStore.Normalize(config);
        if (!valid)
        {
            Assert.Null(config.Profiles.Global.AcPowerPreset);
            return;
        }

        Assert.Equal("fixture", config.Profiles.Global.AcPowerPreset!.PluginId);
        Assert.Equal("balanced", config.Profiles.Global.AcPowerPreset.PresetId);
    }

    [Theory]
    [InlineData(128, 64, true)]
    [InlineData(129, 64, false)]
    [InlineData(128, 65, false)]
    public void AssignmentLengthLimitsApplyAfterTrimming(int pluginLength, int presetLength, bool valid)
    {
        AppConfig config = new();
        config.Profiles.Games.Add(new GameProfile
        {
            Id = "steam:42",
            Enabled = true,
            Values = new ProfileValues
            {
                BatteryPowerPreset = new DevicePowerPresetReference
                {
                    PluginId = " " + new string('p', pluginLength) + " ",
                    PresetId = " " + new string('b', presetLength) + " "
                }
            }
        });
        ConfigStore.Normalize(config);
        var reference = Assert.Single(config.Profiles.Games).Values.BatteryPowerPreset;
        Assert.Equal(valid, reference is not null);
        if (!valid)
        {
            return;
        }

        Assert.Equal(pluginLength, reference!.PluginId.Length);
        Assert.Equal(presetLength, reference.PresetId.Length);
    }

    [Fact]
    public async Task QamAssignmentsShareTheDevicePagePolicyAndClearLocalOverrides()
    {
        Rig rig = new() { Config = { Global = { AcPowerPreset = null } } };
        var assignments = rig.Create();
        var qam = new NativeQamPowerPresetService(rig.Device.Create(), assignments);
        Assert.True((await qam.SetAssignmentAsync(false, "balanced", CancellationToken.None)).Succeeded);
        Assert.Equal("balanced", rig.Config.Global.BatteryPowerPreset?.PresetId);
        Assert.Empty(rig.Device.Calls);
        var state = (await qam.ReadAsync())!;
        Assert.Equal("", state.Ac);
        Assert.Equal("balanced", state.Battery);
        Assert.DoesNotContain(state.Options, option => option.Id == "custom");
        Assert.True((await qam.SetAssignmentAsync(true, "extreme", CancellationToken.None)).Succeeded);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.True((await qam.SetAssignmentAsync(true, null, CancellationToken.None)).Succeeded);
        Assert.Null(rig.Config.Global.AcPowerPreset);
        Assert.False((await qam.SetAssignmentAsync(false, "missing", CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task ReplacedPerGameConfigurationCannotSaveIntoTheGlobalFallback()
    {
        Rig rig = new() { Application = "steam:42" };
        rig.Config.Games.Add(new GameProfile
            { Id = "steam:42", Enabled = true });
        rig.Device.Api.AfterRead = () => rig.Config = new ProfileConfig
            { Global = new ProfileValues { AcPowerPreset = Reference("extreme") } };
        var assignments = rig.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            assignments.AssignAsync(true, "balanced", CancellationToken.None));
        Assert.Equal(0, rig.Saves);
        Assert.Equal("extreme", rig.Config.Global.AcPowerPreset?.PresetId);
        Assert.Empty(rig.Device.Calls);
    }

    [Fact]
    public async Task FailedSecondWriteIsNotRetriedByPolling()
    {
        Rig rig = new() { Device = { FailAt = 2 } };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(31, rig.Device.Views[1].Projection.State.ObservedValue!.IntegerValue);
        await assignments.ReconcileAsync(CancellationToken.None);
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
        rig.Device.OnWriteEntered = count =>
        {
            if (count == 2)
            {
                entered.TrySetResult();
            }
        };
        rig.Device.AfterDeviceWrite = _ => { rig.Device.WaitForWrite = laterWrite; };
        var assignments = rig.Create();
        var applying = assignments.ReconcileAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal(31, rig.Device.Views[1].Projection.State.ObservedValue!.IntegerValue);
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, rig.Device.Calls.Count);
        Assert.Equal(0, rig.Device.Api.Writes);
    }

    [Fact]
    public async Task SourceTransitionsApplyOnceAndDoNotFightManualDrift()
    {
        Rig rig = new();
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, rig.Device.Calls.Count);
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, rig.Device.Calls.Count);
        rig.Device.OnAc = false;
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(4, rig.Device.Calls.Count);
        Assert.Equal(8, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public async Task FailureIsNotRetriedUntilAnExplicitAssignment()
    {
        Rig rig = new() { Device = { FailAt = 1 } };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Single(rig.Device.Calls);
        Assert.NotEmpty(assignments.Snapshot().Status);
        rig.Device.FailAt = 0;
        await assignments.AssignAsync(true, "extreme", CancellationToken.None);
        Assert.Equal(3, rig.Device.Calls.Count);
        Assert.Equal(1, rig.Saves);
    }

    [Fact]
    public async Task PerGameAssignmentsOverrideAndInheritIndependently()
    {
        Rig rig = new() { Application = "steam:42" };
        rig.Config.Games.Add(new GameProfile
            { Id = "steam:42", Enabled = true, Values = new ProfileValues { AcPowerPreset = Reference("balanced") } });
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(17, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        rig.Device.OnAc = false;
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(8, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
        await assignments.AssignAsync(false, "balanced", CancellationToken.None);
        Assert.Equal("balanced", rig.Config.Games[0].Values.BatteryPowerPreset!.PresetId);
        Assert.Equal("battery", rig.Config.Global.BatteryPowerPreset!.PresetId);
        rig.Application = null;
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Equal(8, rig.Device.Views[0].Projection.State.ObservedValue!.IntegerValue);
    }

    [Fact]
    public async Task DisabledIntegrationUnknownSourceAndOtherPluginNeverWrite()
    {
        Rig rig = new() { Enabled = false };
        var assignments = rig.Create();
        await assignments.ReconcileAsync(CancellationToken.None);
        rig.Enabled = true;
        rig.Device.OnAc = null;
        await assignments.ReconcileAsync(CancellationToken.None);
        rig.Device.OnAc = true;
        rig.Plugin = "replacement";
        await assignments.ReconcileAsync(CancellationToken.None);
        Assert.Empty(rig.Device.Calls);
        Assert.Contains("another device", assignments.Snapshot().Status);
    }

    [Fact]
    public void SavedAssignmentsSurviveJson()
    {
        Rig rig = new();
        rig.Config.Games.Add(new GameProfile
            { Id = "steam:42", Enabled = true, Values = new ProfileValues { AcPowerPreset = Reference("balanced") } });
        var json = JsonSerializer.Serialize(rig.Config, ConfigJsonContext.Default.ProfileConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ProfileConfig)!;
        Assert.Equal("extreme", restored.Global.AcPowerPreset!.PresetId);
        Assert.Equal("balanced", Assert.Single(restored.Games).Values.AcPowerPreset!.PresetId);
    }

    private sealed class Rig
    {
        internal readonly DevicePowerPresetsTests.Rig Device = new();
        internal string? Application;

        internal ProfileConfig Config = new()
        {
            Global = new ProfileValues
                { AcPowerPreset = Reference("extreme"), BatteryPowerPreset = Reference("battery") }
        };

        internal bool AutoTdpOwnsPower;
        internal long Cycle = 1;
        internal bool Enabled = true;
        internal string Plugin = "fixture";
        internal int Saves;
        private long _generation;
        private ProfileConfig? _seen;

        internal DevicePowerAssignments Create()
        {
            var presets = Device.Create();
            presets.AutomaticPowerOwner = () => AutoTdpOwnsPower;
            return new DevicePowerAssignments(presets,
                () => new DevicePowerAssignmentContext(Snapshot(), Plugin, Cycle, Enabled, Device.OnAc),
                (context, ac, reference) =>
                {
                    Saves++;
                    var values = context.Profiles.EditsGame
                        ? Config.Games.First(game => game.Id == context.Profiles.Active.GameProfileId).Values
                        : Config.Global;
                    if (ac)
                    {
                        values.AcPowerPreset = reference;
                    }
                    else
                    {
                        values.BatteryPowerPreset = reference;
                    }

                    _generation++;
                    return Task.CompletedTask;
                });
        }

        /// <summary>A snapshot whose generation moves whenever the store is replaced or saved.</summary>
        private ProfileSnapshot Snapshot()
        {
            if (!ReferenceEquals(_seen, Config))
            {
                _seen = Config;
                _generation++;
            }

            return new ProfileSnapshot(Config, ProfileResolver.Activate(Config, Application, null, null), _generation);
        }
    }
}
