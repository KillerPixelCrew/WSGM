using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class DisplayGpuProfileServiceTests
{
    private static DisplayTargetIdentity Monitor(string path, string serial = "serial")
    {
        return new DisplayTargetIdentity(path, null, null, "Same friendly name", 1, 2, 3) { EdidIdentity = serial };
    }

    private static CapabilityDescriptor Depth(string instance)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = "display.color-depth", InstanceId = instance, Role = CapabilityRole.GenericChoice,
            ValueKind = CapabilityValueKind.Choice, SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Color depth" },
            Choices =
            [
                new CapabilityChoice("10", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "10-bit" })
            ]
        };
    }

    private static DisplayGpuCapability Capability(DisplayTargetIdentity? target, string instance)
    {
        return new DisplayGpuCapability
        {
            Target = target, PluginId = "wsgm.gpu.nvidia", Descriptor = Depth(instance)
        };
    }

    [Fact]
    public void ALayoutRematchesThePhysicalDisplayAfterDriverRoutesAndInstanceIdsChange()
    {
        var saved = new DisplayGpuPreference
        {
            Target = Monitor("old-path"), PluginId = "wsgm.gpu.nvidia",
            CapabilityId = "display.color-depth", InstanceId = "old-output", Value = new PluginValue(Text: "10")
        };
        var wrong = Capability(Monitor("old-path", "another-serial"), "old-output");
        var reconnected = Capability(Monitor("new-path") with { TargetId = 99 }, "new-output");

        Assert.Same(reconnected, DisplayGpuProfileService.Resolve(saved, [wrong, reconnected]));
    }

    [Fact]
    public void DuplicateSerialsNeedOneExactCurrentPathAndFriendlyNamesNeverDisambiguate()
    {
        var saved = new DisplayGpuPreference
        {
            Target = Monitor("one"), PluginId = "wsgm.gpu.nvidia", CapabilityId = "display.color-depth"
        };
        var one = Capability(Monitor("one"), "1");
        var two = Capability(Monitor("two"), "2");
        Assert.Same(one, DisplayGpuProfileService.Resolve(saved, [one, two]));
        saved.Target = Monitor("old-disconnected-path");
        Assert.Null(DisplayGpuProfileService.Resolve(saved, [one, two]));
    }

    [Fact]
    public void DriverWideControlsCannotBeMistakenForOneMonitorsControl()
    {
        var saved = new DisplayGpuPreference
        {
            PluginId = "wsgm.gpu.nvidia", CapabilityId = "display.color-depth"
        };
        var global = Capability(null, "driver");
        Assert.Same(global, DisplayGpuProfileService.Resolve(saved, [Capability(Monitor("one"), "1"), global]));
        Assert.Null(DisplayGpuProfileService.Resolve(saved, [Capability(Monitor("one"), "1")]));
    }

    [Fact]
    public void StoredValuesMustPassTheFreshDriversChoices()
    {
        var descriptor = Depth("changed-route");
        Assert.Equal("10",
            DisplayGpuProfileService.ToCapabilityValue(new PluginValue(Text: "10"), descriptor)?.ChoiceValue);
        Assert.Null(DisplayGpuProfileService.ToCapabilityValue(new PluginValue(Text: "12"), descriptor));
        Assert.Null(DisplayGpuProfileService.ToCapabilityValue(new PluginValue(Number: 10), descriptor));
        Assert.Null(DisplayGpuProfileService.ToCapabilityValue(new PluginValue(Text: "10", Boolean: true), descriptor));
    }

    [Fact]
    public void OfflinePreferencesAndRememberedCapabilitiesSurviveTheProductionJsonRoundtrip()
    {
        var display = Monitor("offline");
        var capability = Capability(display, "old-route");
        AppConfig config = new()
        {
            GameModeLaunch = new GameModeLaunchConfiguration
            {
                GameDisplayGpu =
                [
                    new DisplayGpuPreference
                    {
                        Target = display, PluginId = capability.PluginId,
                        CapabilityId = capability.Descriptor!.CapabilityId,
                        InstanceId = "old-route", Value = new PluginValue(Text: "10")
                    }
                ],
                KnownGpuCapabilities = [capability],
                KnownDisplays = [new KnownDisplay { Target = display, GpuCapabilities = [capability] }]
            }
        };

        GameModeLaunchRules.Normalize(config.GameModeLaunch);
        var restored = ConfigJson.Clone(config, ConfigJsonContext.Tolerant.AppConfig);
        Assert.Equal("serial", Assert.Single(restored.GameModeLaunch.GameDisplayGpu).Target?.EdidIdentity);
        Assert.Equal("10", Assert.Single(restored.GameModeLaunch.KnownGpuCapabilities).Descriptor?.Choices[0].Value);
        Assert.Single(Assert.Single(restored.GameModeLaunch.KnownDisplays).GpuCapabilities);
    }

    [Fact]
    public async Task MissingOldHdmiGpuRestorationDoesNotGateEntryButKeepsItsDurableDebt()
    {
        using var store = new TemporaryConfigStore();
        store.Store.Update(config =>
        {
            config.GameModeLaunchRecovery.PendingReturnDisplayGpu =
            [
                new DisplayGpuPreference
                {
                    Target = Monitor("offline"), PluginId = "wsgm.gpu.nvidia", CapabilityId = "display.color-depth",
                    Value = new PluginValue(Text: "8")
                }
            ];
            return true;
        });
        // No audio preference is stored: constructing this borrowed adapter performs no Windows call.
        await using var audio = new AudioProfileService(new CoreAudioProfileOperations());
        List<string> warnings = [];
        var attempts = 0;

        Task<bool> Missing(IReadOnlyList<DisplayGpuPreference> _, CancellationToken cancellationToken)
        {
            ++attempts;
            return Task.FromResult(false);
        }

        Assert.True(await GameModeReturnRecovery.RestorePendingAsync(store.Store, CancellationToken.None,
            audio, warnings.Add, applyGraphics: Missing, requireAudio: false));
        Assert.Equal("8", Assert.Single(store.Store.Read().RequireConfig()
            .GameModeLaunchRecovery.PendingReturnDisplayGpu).Value.Text);
        Assert.Contains(warnings, warning => warning.Contains("display driver controls", StringComparison.Ordinal));
        Assert.False(await GameModeReturnRecovery.RestorePendingAsync(store.Store, CancellationToken.None,
            audio, warnings.Add, applyGraphics: Missing));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExplicitDesktopControlsOverrideOnlyTheirOwnCapturedOriginals()
    {
        using var store = new TemporaryConfigStore();
        var depth = new DisplayGpuPreference
        {
            Target = Monitor("display"), PluginId = "wsgm.gpu.nvidia", CapabilityId = "display.color-depth",
            Value = new PluginValue(Text: "8")
        };
        var dithering = new DisplayGpuPreference
        {
            Target = depth.Target, PluginId = depth.PluginId, CapabilityId = "display.dithering",
            Value = new PluginValue(Text: "off")
        };
        store.Store.Update(config =>
        {
            config.GameModeLaunchRecovery.PendingReturnDisplayGpu = [depth, dithering];
            config.GameModeLaunch.DesktopDisplayGpu =
            [
                new DisplayGpuPreference
                {
                    Target = depth.Target, PluginId = depth.PluginId, CapabilityId = depth.CapabilityId,
                    Value = new PluginValue(Text: "10")
                }
            ];
            return true;
        });
        await using var audio = new AudioProfileService(new CoreAudioProfileOperations());
        IReadOnlyList<DisplayGpuPreference> applied = [];
        Assert.True(await GameModeReturnRecovery.RestorePendingAsync(store.Store, CancellationToken.None, audio,
            applyGraphics: (preferences, _) =>
            {
                applied = preferences;
                return Task.FromResult(true);
            }));
        Assert.Equal(2, applied.Count);
        Assert.Equal("10", applied.Single(item => item.CapabilityId == depth.CapabilityId).Value.Text);
        Assert.Equal("off", applied.Single(item => item.CapabilityId == dithering.CapabilityId).Value.Text);
    }

    [Fact]
    public void ANewEntrySnapshotPreservesUnobservedOriginalsAndDoesNotReplaceThemWithGameModeValues()
    {
        var original = new DisplayGpuPreference
        {
            Target = Monitor("old-route"), PluginId = "wsgm.gpu.nvidia", CapabilityId = "display.color-depth",
            Value = new PluginValue(Text: "8")
        };
        var current = new DisplayGpuPreference
        {
            Target = Monitor("new-route"), PluginId = original.PluginId, CapabilityId = original.CapabilityId,
            Value = new PluginValue(Text: "10")
        };

        Assert.Same(original, Assert.Single(GameModeReturnRecovery.MergeGraphicsOriginals([original], [])));
        Assert.Same(original, Assert.Single(GameModeReturnRecovery.MergeGraphicsOriginals([original], [current])));
        Assert.Same(current, Assert.Single(GameModeReturnRecovery.MergeGraphicsOriginals([], [current])));
    }
}
