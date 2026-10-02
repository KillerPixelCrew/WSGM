// SPDX-License-Identifier: MIT

using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.AmdGpu;

internal sealed unsafe partial class AdlxSession
{
    private static readonly (string Name, string Label, int Supported, int Range, int Read, int Write)[] ColorFields =
    [
        ("hue", "Hue", 3, 4, 5, 6), ("saturation", "Saturation", 7, 8, 9, 10),
        ("brightness", "Brightness", 11, 12, 13, 14), ("contrast", "Contrast", 15, 16, 17, 18),
        ("temperature", "Color temperature", 19, 20, 21, 22)
    ];

    private static readonly string[] VariBrightModes =
        ["Maximum brightness", "Optimize brightness", "Balanced", "Optimize battery", "Maximum battery"];

    private static readonly string[] DitherModes =
    [
        "Disabled", "Driver default", "Frame modulation 6-bit", "Frame modulation 8-bit", "Frame modulation 10-bit",
        "Dither 6-bit", "Dither 8-bit", "Dither 10-bit", "Dither 6-bit without frame randomization",
        "Dither 8-bit without frame randomization", "Dither 10-bit without frame randomization",
        "Truncate to 6-bit", "Truncate to 8-bit", "Truncate to 10-bit", "Truncate 8 / dither 8",
        "Truncate 10 / dither 6", "Truncate 10 / frame modulation 8", "Truncate 10 / frame modulation 6",
        "Truncate 10 / dither 8 / frame modulation 6", "Dither 10 / frame modulation 8",
        "Dither 10 / frame modulation 6",
        "Truncate 8 / dither 6", "Truncate 8 / frame modulation 6", "Dither 8 / frame modulation 6"
    ];

    private void AddDisplay(nint display, AmdTarget target, string instance, string section,
        List<AdlxObject> objects, List<DriverControl> controls)
    {
        var service = _displays!.Pointer;
        Feature(service, 9, "freesync", feature => Toggle(feature, "variable-refresh-rate", "AMD FreeSync",
            CapabilityRole.VariableRefreshRate));
        Feature(service, 10, "virtual-super-resolution",
            feature => Toggle(feature, "virtual-super-resolution", "Virtual Super Resolution"));
        Feature(service, 11, "gpu-scaling", feature => Toggle(feature, "gpu-scaling", "GPU scaling"));
        Feature(service, 12, "scaling-mode", feature => Choice(feature, "scaling-mode", "Scaling mode", 4, 5,
            [(0, "Preserve aspect ratio"), (1, "Full panel"), (2, "Centered")]));
        Feature(service, 13, "integer-scaling", feature => Toggle(feature, "integer-scaling", "Integer scaling"));
        Feature(service, 14, "color-depth", feature => Choice(feature, "color-depth", "Output bit depth", 4, 5,
            new[] { (1, "6 bpc"), (2, "8 bpc"), (3, "10 bpc"), (4, "12 bpc"), (5, "14 bpc"), (6, "16 bpc") }
                .Where(value => AdlxNative.SupportsValue(feature.Pointer, 6, value.Item1)).ToArray(), 6));
        Feature(service, 15, "pixel-format", feature => Choice(feature, "pixel-format", "Output pixel format", 4, 5,
            new[]
                {
                    (1, "RGB 4:4:4 full"), (2, "YCbCr 4:4:4"), (3, "YCbCr 4:2:2"),
                    (4, "RGB 4:4:4 limited"), (5, "YCbCr 4:2:0")
                }
                .Where(value => AdlxNative.SupportsValue(feature.Pointer, 6, value.Item1)).ToArray(), 6));
        Feature(service, 16, "custom-color", feature =>
        {
            foreach (var (name, label, supported, range, read, write) in ColorFields)
            {
                Try(instance + "/color-" + name, () =>
                {
                    if (AdlxNative.Boolean(feature.Pointer, supported))
                    {
                        Range(feature, "color-" + name, label, supported, range, read, write);
                    }
                });
            }
        }, -1);
        Feature(service, 19, "vari-bright", feature =>
        {
            Toggle(feature, "vari-bright", "Vari-Bright");
            controls.Add(new AdlxControl(DriverDescriptors.Choice("display.vari-bright-mode", instance,
                    "Vari-Bright policy",
                    section, CapabilityProfileScope.GlobalOnly,
                    VariBrightModes.Select((label, index) => (AmdValue.Encode(index), label))),
                () =>
                {
                    for (var index = 0; index < VariBrightModes.Length; index++)
                    {
                        if (AdlxNative.Boolean(feature.Pointer, 6 + index))
                        {
                            return CapabilityValue.Choice(AmdValue.Encode(index));
                        }
                    }

                    throw new DriverFailure("The Vari-Bright policy could not be read.");
                }, value =>
                {
                    Validate(target);
                    RequireSupported(feature);
                    AdlxNative.Call(feature.Pointer, 11 + AmdValue.Decode(value));
                }));
        });
        if (_displays3 is not null)
        {
            Feature(_displays3.Pointer, 23, "freesync-color-accuracy", feature => Toggle(feature,
                "freesync-color-accuracy", "FreeSync color accuracy"));
        }

        if (_dither is not null && _mapping != 0)
        {
            Try(instance + "/dithering", () =>
            {
                var ids = AdlIds(display);
                _dither.Read(ids.Adapter, ids.Display);
                controls.Add(new AdlxControl(DriverDescriptors.Choice("display.dithering", instance, "Dithering",
                        section,
                        CapabilityProfileScope.GlobalOnly,
                        DitherModes.Select((label, index) => (AmdValue.Encode(index), label))),
                    () =>
                    {
                        var current = AdlIds(display);
                        return CapabilityValue.Choice(AmdValue.Encode(_dither.Read(current.Adapter, current.Display)));
                    }, value =>
                    {
                        Validate(target);
                        var current = AdlIds(display);
                        _dither.Write(current.Adapter, current.Display, AmdValue.Decode(value));
                    }));
            });
        }

        return;

        void Feature(nint owner, int slot, string id, Action<AdlxObject> build, int supported = 3)
        {
            Try(instance + "/" + id, () =>
            {
                var feature = AdlxNative.Feature(owner, slot, display);
                objects.Add(feature);
                if (supported < 0 || AdlxNative.Boolean(feature.Pointer, supported))
                {
                    build(feature);
                }
            });
        }

        void Toggle(AdlxObject feature, string id, string label, CapabilityRole role = CapabilityRole.GenericToggle)
        {
            controls.Add(new AdlxControl(DriverDescriptors.Toggle("display." + id, instance, label, section,
                        CapabilityProfileScope.GlobalOnly) with
                    {
                        Role = role
                    },
                () => CapabilityValue.Boolean(AdlxNative.Boolean(feature.Pointer, 4)), value =>
                {
                    Validate(target);
                    RequireSupported(feature);
                    AdlxNative.SetBoolean(feature.Pointer, 5, value.BooleanValue == true);
                }));
        }

        void Choice(AdlxObject feature, string id, string label, int read, int write,
            IReadOnlyList<(int Value, string Label)> values, int? supportedValue = null)
        {
            if (values.Count == 0)
            {
                return;
            }

            controls.Add(new AdlxControl(DriverDescriptors.Choice("display." + id, instance, label, section,
                    CapabilityProfileScope.GlobalOnly,
                    values.Select(value => (AmdValue.Encode(value.Value), value.Label))),
                () => CapabilityValue.Choice(AmdValue.Encode(AdlxNative.Integer(feature.Pointer, read))), value =>
                {
                    Validate(target);
                    RequireSupported(feature);
                    var requested = AmdValue.Decode(value);
                    if (supportedValue is { } slot && !AdlxNative.SupportsValue(feature.Pointer, slot, requested))
                    {
                        throw new DriverFailure("This output color value is no longer supported by the AMD link.");
                    }

                    AdlxNative.SetInteger(feature.Pointer, write, requested);
                }));
        }

        void Range(AdlxObject feature, string id, string label, int supported, int range, int read, int write)
        {
            var bounds = AdlxNative.Range(feature.Pointer, range);
            controls.Add(new AdlxControl(DriverDescriptors.Range("display." + id, instance, label, section,
                    CapabilityProfileScope.GlobalOnly, bounds.Minimum, bounds.Maximum, bounds.Step),
                () => CapabilityValue.Integer(AdlxNative.Integer(feature.Pointer, read)), value =>
                {
                    Validate(target);
                    RequireSupported(feature, supported);
                    if (!Within(value.IntegerValue!.Value, AdlxNative.Range(feature.Pointer, range)))
                    {
                        throw new DriverFailure("This color value is outside the current AMD range.");
                    }

                    AdlxNative.SetInteger(feature.Pointer, write, value.IntegerValue.Value);
                }));
        }
    }

    private (int Adapter, int Display) AdlIds(nint display)
    {
        int adapter = 0, output = 0, bus = 0, device = 0, function = 0;
        AdlxNative.Check(((delegate* unmanaged[Stdcall]<nint, nint, int*, int*, int*, int*, int*, int>)
                AdlxNative.Function(_mapping, 5))(_mapping, display, &adapter, &output, &bus, &device, &function),
            "ADLIdsFromADLXDisplay");
        return (adapter, output);
    }
}
