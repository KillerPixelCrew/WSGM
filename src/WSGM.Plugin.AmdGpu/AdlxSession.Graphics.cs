// SPDX-License-Identifier: MIT

using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;

namespace WSGM.Plugin.AmdGpu;

internal sealed partial class AdlxSession
{
    private void AddGraphics(nint gpu, AmdTarget target, string instance, string section,
        List<AdlxObject> objects, List<DriverControl> controls)
    {
        if (_graphics is null)
        {
            return;
        }

        var service = _graphics.Pointer;
        Feature(service, 3, "anti-lag", feature =>
        {
            Toggle(feature, "anti-lag", "Radeon Anti-Lag", 4, 5);
            var next = AdlxNative.Query(feature.Pointer, "IADLX3DAntiLag1");
            if (next is not null)
            {
                objects.Add(next);
                Choice(next, "anti-lag-level", "Anti-Lag level", 6, 7,
                    [(0, "Anti-Lag"), (1, "Anti-Lag Next")]);
            }
        });
        Feature(service, 5, "boost", feature =>
        {
            Toggle(feature, "boost", "Radeon Boost", 4, 7);
            Range(feature, "boost-resolution", "Boost minimum resolution", 5, 6, 8);
        });
        Feature(service, 6, "image-sharpening", feature =>
        {
            Toggle(feature, "image-sharpening", "Radeon Image Sharpening", 4, 7);
            Range(feature, "sharpness", "Image sharpening strength", 5, 6, 8);
        });
        Feature(service, 7, "enhanced-sync", feature => Toggle(feature, "enhanced-sync", "Enhanced Sync", 4, 5));
        Feature(service, 8, "vertical-sync", feature => Choice(feature, "vertical-sync", "Wait for vertical refresh", 5,
            6,
            [
                (0, "Always off"), (1, "Off unless application specifies"), (2, "On unless application specifies"),
                (3, "Always on")
            ]));
        Feature(service, 10, "anti-aliasing", feature =>
        {
            Choice(feature, "anti-aliasing-mode", "Anti-aliasing mode", 4, 7,
            [
                (0, "Application settings"), (1, "Enhance application settings"), (2, "Override application settings")
            ]);
            Choice(feature, "anti-aliasing-level", "Anti-aliasing level", 5, 8,
                [(2, "2x"), (3, "2x EQ"), (4, "4x"), (5, "4x EQ"), (8, "8x"), (9, "8x EQ")]);
            Choice(feature, "anti-aliasing-method", "Anti-aliasing method", 6, 9,
                [(0, "Multisampling"), (1, "Adaptive multisampling"), (2, "Supersampling")]);
        });
        Feature(service, 11, "morphological-aa",
            feature => Toggle(feature, "morphological-aa", "Morphological anti-aliasing", 4, 5));
        Feature(service, 12, "anisotropic-filtering", feature =>
        {
            Toggle(feature, "anisotropic-filtering", "Anisotropic filtering", 4, 6);
            Choice(feature, "anisotropic-level", "Anisotropic filtering level", 5, 7,
                [(2, "2x"), (4, "4x"), (8, "8x"), (16, "16x")]);
        });
        Feature(service, 13, "tessellation", feature =>
        {
            Choice(feature, "tessellation-mode", "Tessellation mode", 4, 6,
                [(0, "AMD optimized"), (1, "Application settings"), (2, "Override application settings")]);
            Choice(feature, "tessellation-level", "Tessellation maximum level", 5, 7,
                [(1, "Off"), (2, "2x"), (4, "4x"), (6, "6x"), (8, "8x"), (16, "16x"), (32, "32x"), (64, "64x")]);
        });
        Feature(service, 14, "rsr", feature =>
        {
            Toggle(feature, "rsr", "Radeon Super Resolution", 4, 5);
            Range(feature, "rsr-sharpness", "RSR sharpness", 6, 7, 8);
        });
        Feature(service, 15, "reset-shader-cache", feature =>
        {
            var descriptor = DriverDescriptors.Toggle("graphics.reset-shader-cache", instance, "Reset shader cache",
                    section,
                    CapabilityProfileScope.GlobalOnly) with
                {
                    Role = CapabilityRole.GenericAction, ValueKind = CapabilityValueKind.None, SupportsRead = false,
                    SupportsWrite = false, SupportsAction = true, Persistence = CapabilityPersistence.Volatile
                };
            controls.Add(new AdlxControl(descriptor, CapabilityValue.None, (_, admission) =>
            {
                Validate(target);
                RequireSupported(feature);
                AdlxNative.Call(feature.Pointer, 4, admission);
            }));
        });
        if (_graphics1 is not null)
        {
            Feature(_graphics1.Pointer, 17, "afmf", feature =>
            {
                Toggle(feature, "afmf", "AMD Fluid Motion Frames", 4, 5);
                var advanced = AdlxNative.Query(feature.Pointer, "IADLX3DAMDFluidMotionFrames1");
                if (advanced is not null)
                {
                    objects.Add(advanced);
                    if (AdlxNative.Boolean(advanced.Pointer, 6))
                    {
                        Choice(advanced, "afmf-algorithm", "AFMF algorithm", 7, 8,
                            [(0, "Auto"), (1, "Enhanced (AFMF 2.1)"), (2, "Standard")]);
                        Choice(advanced, "afmf-search-mode", "AFMF search mode", 9, 10,
                            [(0, "Auto"), (1, "Standard"), (2, "High")]);
                        Choice(advanced, "afmf-performance-mode", "AFMF performance mode", 11, 12,
                            [(0, "Auto"), (1, "Quality"), (2, "Performance")]);
                        Choice(advanced, "afmf-fast-motion", "AFMF fast motion response", 13, 14,
                            [(0, "Repeat frames"), (1, "Blend frames")]);
                    }
                }
            });
        }

        if (_graphics2 is not null)
        {
            Feature(_graphics2.Pointer, 18, "desktop-sharpening", feature =>
                Toggle(feature, "desktop-sharpening", "Desktop image sharpening", 4, 5,
                    scope: CapabilityProfileScope.GlobalOnly));
        }

        if (_graphics3 is not null)
        {
            Feature(_graphics3.Pointer, 19, "fsr-upgrade", feature =>
                Toggle(feature, "fsr-upgrade", "FidelityFX upscaling upgrade", 4, 5));
            Feature(_graphics3.Pointer, 20, "fsr-framegen-upgrade", feature =>
            {
                Toggle(feature, "fsr-framegen-upgrade", "FidelityFX frame generation upgrade", 6, 7, 5);
                using var list = AdlxNative.Interface(feature.Pointer, 3);
                var items = AdlxNative.Items(list.Pointer);
                try
                {
                    var values = items.Select(item => AdlxNative.Integer(item.Pointer, 3)).Distinct()
                        .Where(ratio => ratio == 1).Select(ratio => (ratio, "2x")).ToArray();
                    if (values.Length > 0)
                    {
                        Choice(feature, "fsr-framegen-ratio", "FidelityFX frame generation ratio", 4, 8,
                            values, 5);
                    }
                }
                finally
                {
                    Release(items);
                }
            }, 5);
        }

        // Chill and FRTC are deliberately absent: WSGM/RTSS remain the frame-limit owner.
        return;

        void Feature(nint owner, int slot, string id, Action<AdlxObject> build, int supported = 3)
        {
            Try(instance + "/" + id, () =>
            {
                var feature = AdlxNative.GraphicsFeature(owner, slot, gpu);
                objects.Add(feature);
                if (AdlxNative.Boolean(feature.Pointer, supported))
                {
                    build(feature);
                }
            });
        }

        void Toggle(AdlxObject feature, string id, string label, int read, int write, int supported = 3,
            CapabilityProfileScope scope = CapabilityProfileScope.Switched)
        {
            controls.Add(new AdlxControl(DriverDescriptors.Toggle("graphics." + id, instance, label, section, scope),
                () => CapabilityValue.Boolean(AdlxNative.Boolean(feature.Pointer, read)), (value, admission) =>
                {
                    Validate(target);
                    RequireSupported(feature, supported);
                    AdlxNative.SetBoolean(feature.Pointer, write, value.BooleanValue == true, admission);
                }));
        }

        void Choice(AdlxObject feature, string id, string label, int read, int write,
            IReadOnlyList<(int Value, string Label)> values, int supported = 3)
        {
            controls.Add(new AdlxControl(DriverDescriptors.Choice("graphics." + id, instance, label, section,
                    CapabilityProfileScope.Switched,
                    values.Select(value => (AmdValue.Encode(value.Value), value.Label))),
                () => CapabilityValue.Choice(AmdValue.Encode(AdlxNative.Integer(feature.Pointer, read))),
                (value, admission) =>
                {
                    Validate(target);
                    RequireSupported(feature, supported);
                    AdlxNative.SetInteger(feature.Pointer, write, AmdValue.Decode(value), admission);
                }));
        }

        void Range(AdlxObject feature, string id, string label, int range, int read, int write)
        {
            var bounds = AdlxNative.Range(feature.Pointer, range);
            controls.Add(new AdlxControl(DriverDescriptors.Range("graphics." + id, instance, label, section,
                        CapabilityProfileScope.Switched, bounds.Minimum, bounds.Maximum, bounds.Step) with
                    {
                        Unit = CapabilityUnit.Percent
                    },
                () => CapabilityValue.Integer(AdlxNative.Integer(feature.Pointer, read)), (value, admission) =>
                {
                    Validate(target);
                    RequireSupported(feature);
                    var current = AdlxNative.Range(feature.Pointer, range);
                    if (!Within(value.IntegerValue!.Value, current))
                    {
                        throw new DriverFailure("This value is outside the AMD driver's current range.");
                    }

                    AdlxNative.SetInteger(feature.Pointer, write, value.IntegerValue.Value, admission);
                }));
        }
    }

    private static void RequireSupported(AdlxObject feature, int supported = 3)
    {
        if (!AdlxNative.Boolean(feature.Pointer, supported))
        {
            throw new DriverFailure("This AMD feature is no longer supported on the selected target.");
        }
    }

    internal static bool Within(int value, AdlxRange range)
    {
        return value >= range.Minimum && value <= range.Maximum
                                      && ((long)value - range.Minimum) % range.Step == 0;
    }
}
