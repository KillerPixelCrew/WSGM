// SPDX-License-Identifier: MIT
// Curated with NoVidiaApp 60d93c9; public values are checked against NvApiDriverSettings.h.

namespace WSGM.Plugin.NvidiaGpu;

internal sealed record NvSettingDefinition(
    uint Id,
    string Label,
    string Group,
    bool Native,
    bool Documented,
    IReadOnlyList<(uint Value, string Label)> Values);

internal static class NvSettings
{
    internal static readonly NvSettingDefinition[] All =
    [
        new(0x1057eb71, "Power management", "performance", true, true,
        [
            (0x00000000, "Adaptive"), (0x00000001, "Prefer maximum performance"), (0x00000002, "Driver controlled"),
            (0x00000003, "Prefer consistent performance"), (0x00000004, "Prefer minimum power"),
            (0x00000005, "Optimal power")
        ]),
        new(0x20c1221e, "Threaded Optimization", "performance", true, true,
            [(0x00000000, "Auto"), (0x00000001, "On"), (0x00000002, "Off")]),
        new(0x007ba09e, "Maximum Pre-Rendered Frames", "performance", true, true,
        [
            (0x00000000, "Application controlled"), (0x00000001, "1"), (0x00000002, "2"), (0x00000003, "3"),
            (0x00000004, "4")
        ]),
        // NoVidiaApp's 0x0005f543 is CPL bookkeeping. ColorControl/NPI identify the actual enable bit.
        new(0x10835000, "Ultra low latency", "performance", true, false,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x00ac8497, "Shader Cache Size", "performance", false, true,
        [
            (0x00000000, "Disabled"), (0x00000400, "1 GB"), (0x00000800, "2 GB"), (0x00001000, "4 GB"),
            (0x00002000, "8 GB"), (0x00002800, "10 GB"), (0x00005000, "20 GB"), (0x0000c800, "50 GB"),
            (0x00019000, "100 GB"), (0xffffffff, "Unlimited")
        ]),
        new(0x000f00ba, "Resizable BAR", "performance", true, false,
            [(0x00000000, "Disabled"), (0x00000001, "Enabled")]),
        new(0x000f00ff, "Resizable BAR - Size Limit", "performance", true, false,
        [
            (0x00000000, "Default"), (0x0c800000, "200 MB"), (0x40000000, "1 GB"), (0x60000000, "1.5 GB"),
            (0x80000000, "2 GB")
        ]),
        new(0x00e942fc, "Resizable BAR - Intel CPU Exclusion", "performance", true, false,
            [(0x00000000, "Allow Intel"), (0x00000001, "Disallow Intel")]),
        new(0x00e942fe, "Resizable BAR - DirectX Laptop Workaround", "performance", true, false,
            [(0x00000000, "Disabled"), (0x00000001, "Enabled")]),
        new(0x20feaf0d, "Resizable BAR - Vulkan Laptop Workaround", "performance", true, false,
            [(0x00000000, "Disabled"), (0x00000001, "Enabled")]),
        new(0x101e61a9, "Anisotropic Filtering", "quality", true, true,
        [
            (0x00000000, "Off"), (0x00000001, "Application controlled"), (0x00000002, "2x"),
            (0x00000004, "4x"), (0x00000008, "8x"), (0x00000010, "16x")
        ]),
        new(0x00ce2691, "Texture Filtering Quality", "quality", true, true,
        [
            (0xfffffff6, "High Quality"), (0x00000000, "Quality"), (0x0000000a, "Performance"),
            (0x00000014, "High Performance")
        ]),
        new(0x0019bb68, "Texture Filtering - Negative LOD Bias", "quality", true, true,
            [(0x00000000, "Allow"), (0x00000001, "Clamp")]),
        new(0x107efc5b, "Antialiasing Mode", "quality", true, true,
            [(0x00000000, "Application controlled"), (0x00000001, "Override"), (0x00000002, "Enhance")]),
        new(0x107d639d, "Antialiasing - Gamma Correction", "quality", true, true,
            [(0x00000000, "Off"), (0x00000001, "On in fullscreen"), (0x00000002, "Always on")]),
        new(0x1074c972, "FXAA", "quality", true, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x00667329, "Ambient Occlusion", "quality", true, true,
            [(0x00000000, "Off"), (0x00000001, "Low"), (0x00000002, "Medium"), (0x00000003, "High")]),
        new(0x10444444, "NVIDIA Image Scaling (NIS)", "quality", true, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x00a879cf, "Vertical Sync", "display", true, true,
        [
            (0x60925292, "Application controlled"), (0x08416747, "Off"), (0x47814940, "On"),
            (0x32610244, "Half refresh rate"), (0x71271021, "One third refresh rate"),
            (0x13245256, "One quarter refresh rate"), (0x18888888, "Fast Sync")
        ]),
        new(0x005a375c, "Vertical Sync - Adaptive", "display", true, true,
            [(0x96861077, "Disable"), (0x99941284, "Enable")]),
        new(0x20fdd1f9, "Triple Buffering", "display", true, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x1194f158, "G-SYNC Mode", "display", false, true,
            [(0x00000000, "Disabled"), (0x00000001, "Fullscreen Only"), (0x00000002, "Fullscreen and Windowed")]),
        new(0x1094f157, "G-SYNC enabled", "display", false, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x20d690f8, "Vulkan/OpenGL Present Method", "display", true, true,
            [(0x00000000, "Prefer Native"), (0x00000001, "Prefer DXGI Swapchain"), (0x00000002, "Auto")]),
        new(0x20324987, "Vulkan/OpenGL Present Method - Flags", "display", true, false,
        [
            (0x00000000, "None"), (0x00000001, "Disable Fullscreen Optimization"),
            (0x00000002, "Disable Present Thread"), (0x00000004, "Enable Direct Flip Always"),
            (0x00000008, "Enable Non-Stereo DX Present"), (0x00080000, "Allow DXVK Promotion"),
            (0x00080004, "Allow DXVK to DXGI/DirectFlip"), (0x10000000, "Enable Win7 Fullscreen Stereo")
        ]),
        new(0x2072c5a3, "OpenGL GDI Compatibility", "display", true, true,
            [(0x00000000, "Prefer Disabled"), (0x00000001, "Prefer Enabled"), (0x00000002, "Auto")]),
        new(0x10e41e01, "DLSS Super Resolution Override", "dlss", true, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x10e41e02, "DLSS Ray Reconstruction Override", "dlss", true, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x10e41e03, "DLSS Frame Generation Override", "dlss", true, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x104d6667, "DLSS-FG Multi-Frame Generation", "dlss", true, true,
            [(0x00000000, "Default"), (0x00000001, "2x"), (0x00000002, "3x"), (0x00000003, "4x")]),
        new(0x10e41df3, "DLSS Preset", "dlss", true, true,
        [
            (0x00000000, "Default"), (0x00000001, "Preset A"), (0x00000002, "Preset B"), (0x00000003, "Preset C"),
            (0x00000004, "Preset D"), (0x00000005, "Preset E"), (0x00000006, "Preset F"), (0x00000007, "Preset G"),
            (0x00000008, "Preset H"), (0x00000009, "Preset I"), (0x0000000a, "Preset J (DLSS 4)"),
            (0x0000000b, "Preset K (DLSS 4)"), (0x0000000c, "Preset L (DLSS 4)"), (0x0000000d, "Preset M (DLSS 4)"),
            (0x0000000e, "Preset N"), (0x0000000f, "Preset O"), (0x00ffffff, "Always Use Latest")
        ]),
        new(0x10afb768, "DLSS Quality Mode", "dlss", true, true,
        [
            (0x00000000, "Performance"), (0x00000001, "Balanced"), (0x00000002, "Quality"),
            (0x00000003, "Application controlled"), (0x00000004, "DLAA"), (0x00000005, "Ultra Performance"),
            (0x00000006, "Ngx Dlss Sr Mode Custom"), (0x00000007, "Ngx Dlss Sr Mode Reserved A")
        ]),
        new(0x10e41df7, "DLSS-RR Preset", "dlss", true, true,
        [
            (0x00000000, "Default"), (0x00000001, "Preset A"), (0x00000002, "Preset B"), (0x00000003, "Preset C"),
            (0x00000004, "Preset D"), (0x00000005, "Preset E"), (0x00000006, "Preset F"), (0x00000007, "Preset G"),
            (0x00000008, "Preset H"), (0x00000009, "Preset I"), (0x0000000a, "Preset J (DLSS 4)"),
            (0x0000000b, "Preset K (DLSS 4)"), (0x0000000c, "Preset L (DLSS 4)"), (0x0000000d, "Preset M (DLSS 4)"),
            (0x0000000e, "Preset N"), (0x0000000f, "Preset O"), (0x00ffffff, "Always Use Latest")
        ]),
        new(0x10bd9423, "DLSS-RR Quality Mode", "dlss", true, true,
        [
            (0x00000000, "Performance"), (0x00000001, "Balanced"), (0x00000002, "Quality"),
            (0x00000003, "Application controlled"), (0x00000004, "DLAA"), (0x00000005, "Ultra Performance"),
            (0x00000006, "Ngx Dlss Rr Mode Custom")
        ]),
        new(0xb0d384c0, "Smooth Motion", "dlss", true, false,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x00dd48fb, "RTX HDR", "dlss", true, false,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x00980880, "RTX Digital Vibrance", "dlss", true, false,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x10111133, "VR Pre-Rendered Frames", "vr", true, true,
        [
            (0x00000000, "Application controlled"), (0x00000001, "1"), (0x00000002, "2"), (0x00000003, "3"),
            (0x00000004, "4")
        ]),
        new(0x1095f16f, "G-SYNC indicator", "display", false, true,
            [(0x00000000, "Off"), (0x00000001, "On")]),
        new(0x1094f1f7, "G-SYNC requested policy", "display", false, true,
            [(0x00000000, "Off"), (0x00000001, "Fullscreen"), (0x00000002, "Fullscreen and windowed")]),
        new(0x10a879cf, "G-SYNC application policy", "display", true, true,
        [
            (0x00000000, "Allow"), (0x00000001, "Force off"), (0x00000002, "Disallow"), (0x00000003, "ULMB"),
            (0x00000004, "Fixed refresh")
        ]),
        new(0x10a879ac, "G-SYNC requested application policy", "display", true, true,
        [
            (0x00000000, "Allow"), (0x00000001, "Force off"), (0x00000002, "Disallow"), (0x00000003, "ULMB"),
            (0x00000004, "Fixed refresh")
        ])
    ];
}
