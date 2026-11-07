using System.Diagnostics;
using LibGPUDriverInteract;
using SteamUiToolkit;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed partial class SteamNativeSettingsServiceTests
{
    /// <summary>
    ///     Every row the producer builds, on all four pages, through the gate that ships: unknown values, the
    ///     Atlas 8's controller lighting, a fan curve, the audio format rows and one row the gate must refuse.
    /// </summary>
    /// <remarks>
    ///     The earlier offline check published only rows with values, so it never saw the two Intel colour
    ///     ranges whose read failed. The gate refused the whole publication for them and every WSGM row
    ///     left Steam's Settings, the Atlas's lighting and audio rows included.
    /// </remarks>
    [Fact]
    public async Task EveryProducedRowReachesTheEmittedGateAndOneBadRowLeavesTheRestDrawn()
    {
        await using var rig = await ProducerRig.CreateAsync();
        var produced = (await rig.Producer.ReadAsync())!;

        SteamSettingsRow Gpu(string id, string label)
        {
            return SteamCapabilityRows.Row(UnknownGpuRange(id, label), "gpu/wsgm.gpu.intel/" + id + "#display0");
        }

        var lighting = AtlasLighting().Select(SteamNativeSettingsService.DeviceRow).ToList();
        var stereo = new CoreAudio.AudioDeviceFormat(2, 48000, 24, 32, 3, false);
        var audio = SteamNativeSettingsService.AudioPlaybackRows("audio/ENDPOINT",
                NativeQamAudioFormatService.Choices(new AudioPlaybackCapabilities("endpoint",
                    [stereo, stereo with { SampleRate = 96000 }, stereo with { Channels = 6, ChannelMask = 0x60F }],
                    stereo, [CoreAudio.SpatialAudioFormats.Off], Guid.Empty)))
            .Select(entry => entry.Row).ToList();
        var invalid = new SteamSettingsRow("contract.invalid", "unsupported-kind", "Not drawable");

        List<SteamNativeSettingsPage> pages = [];
        foreach (var id in new[]
                 {
                     SteamNativeSettingsPageId.Display, SteamNativeSettingsPageId.Power,
                     SteamNativeSettingsPageId.Audio, SteamNativeSettingsPageId.Controller
                 })
        {
            List<SteamSettingsSection> sections =
                [.. produced.Pages.SingleOrDefault(page => page.Id == id)?.Sections ?? []];
            if (id == SteamNativeSettingsPageId.Display)
            {
                sections.Add(new SteamSettingsSection("Intel graphics: Colour",
                    [Gpu("display.color-hue", "Hue"), Gpu("display.color-saturation", "Saturation"), invalid],
                    "gpu/wsgm.gpu.intel/display/color"));
            }
            else if (id == SteamNativeSettingsPageId.Audio)
            {
                sections.Add(new SteamSettingsSection("Advanced playback", audio, "advanced-playback"));
            }
            else if (id == SteamNativeSettingsPageId.Controller)
            {
                sections.Add(new SteamSettingsSection("Controller lighting", lighting, "device/acer/lighting"));
            }
            else
            {
                sections.Add(new SteamSettingsSection("Fan",
                    [SteamNativeSettingsService.DeviceRow(FanCurve())], "device/acer/fan"));
            }

            pages.Add(new SteamNativeSettingsPage(id, sections));
        }

        var state = new SteamNativeSettingsState(pages, produced.Revision + 1);
        Assert.Contains(lighting, row => row.Kind == SteamSettingsRowKind.Color);
        Assert.Contains(lighting, row => row is { Kind: SteamSettingsRowKind.Range, Number: null });
        Assert.Equal(["Channels", "Format", "Spatial sound"], audio.Select(row => row.Label));
        Assert.All(pages, page => Assert.NotEmpty(page.Sections));

        using var temporary = new TemporaryDirectory();
        var payload = Path.Combine(temporary.Root, "native-settings.json");
        await File.WriteAllTextAsync(payload, SteamNativeSettingsSurface.Serialize(state).GetRawText());
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = WSGM.Testing.RepositoryFiles.Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("tests/WSGM.Tests/Shell/Steam/native-settings-contract.mjs");
        start.ArgumentList.Add(payload);
        start.ArgumentList.Add(invalid.Key);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    // A colour range whose read failed: the Intel publisher reports it with no value (c3fdb4c8).
    private static DeviceOverlayCapability UnknownGpuRange(string id, string label)
    {
        var descriptor = GpuDriverPublisher.ToHost(new GpuDescriptorSet
        {
            Sections =
            [
                new GpuSection
                {
                    SectionId = "display", Title = "Display", Kind = GpuSectionKind.Display,
                    Categories = [new GpuCategory { CategoryId = "color", Title = "Colour" }]
                }
            ],
            Descriptors =
            [
                new GpuDescriptor
                {
                    CapabilityId = id, InstanceId = "display0", Label = label, Role = GpuRole.Range,
                    ValueKind = GpuValueKind.Integer, Minimum = 0, Maximum = 100, Step = 1, SectionId = "display",
                    CategoryId = "color", SupportsRead = true, SupportsWrite = true
                }
            ]
        }).Descriptors.Single();
        var state = GpuDriverPublisher.ToHost(new GpuState
        {
            CapabilityId = id, InstanceId = "display0", Available = true,
            Reason = new GpuReason(GpuReasonCode.TransportFaulted, "the driver answered 0x40000017"),
            Quality = GpuStateQuality.Unknown
        });
        return CapabilityRowProjection.ToOverlayCapability(
            new DeviceCapabilityView(descriptor, new CapabilityProjection { State = state }, null),
            new HashSet<string>(), FanCurvePresetTable.HandheldCompanion);
    }

    // The Atlas 8's controller lighting as LibHandheld's AcerLightingService declares it: brightness,
    // speed, effect, the whole ring and the eight measured segments. Speed has not been read yet and
    // the effect is one the firmware has no runtime control for, so both arrive without a value.
    private static IEnumerable<DeviceOverlayCapability> AtlasLighting()
    {
        yield return Capability(CapabilityIds.LightingBrightness, null, CapabilityValueKind.Integer,
            CapabilityRole.LightingBrightness, "Brightness", CapabilityValue.Integer(80), 0, 100);
        yield return Capability(CapabilityIds.LightingSpeed, null, CapabilityValueKind.Integer,
            CapabilityRole.LightingEffectSpeed, "Lighting speed", null, 0, 20);
        yield return Capability(CapabilityIds.LightingEffect, null, CapabilityValueKind.Choice,
            CapabilityRole.LightingEffect, "Lighting effect", null) with
        {
            Choices =
            [
                new CapabilityChoice("solid", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Solid" }),
                new CapabilityChoice("breathing",
                    new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Breathing" })
            ]
        };
        yield return Capability(CapabilityIds.LightingColor, "main", CapabilityValueKind.Color,
            CapabilityRole.GenericReadOnly, "All ring segments", null);
        foreach (var zone in new[]
                 {
                     "left.top-left", "left.top-right", "left.bottom-left", "left.bottom-right", "right.top-left",
                     "right.top-right", "right.bottom-left", "right.bottom-right"
                 })
        {
            yield return Capability(CapabilityIds.LightingColor, zone, CapabilityValueKind.Color,
                CapabilityRole.GenericReadOnly, zone, CapabilityValue.Color(0x00FF80));
        }
    }

    private static DeviceOverlayCapability FanCurve()
    {
        return Capability(CapabilityIds.FanCurve, null, CapabilityValueKind.Curve, CapabilityRole.FanCurve,
            "Fan curve", CapabilityValue.Curve([new CurvePoint(40, 20), new CurvePoint(60, 50), new CurvePoint(80, 90)]),
            0, 100);
    }

    private static DeviceOverlayCapability Capability(string id, string? instance, CapabilityValueKind kind,
        CapabilityRole role, string title, CapabilityValue? value, int? minimum = null, int? maximum = null)
    {
        return new DeviceOverlayCapability(id, instance, DescriptorStatus.Available, title, string.Empty, string.Empty,
            true, value)
        {
            Writable = true, ValueKind = kind, Role = role, Minimum = minimum, Maximum = maximum,
            Step = minimum is null ? null : 1
        };
    }
}
