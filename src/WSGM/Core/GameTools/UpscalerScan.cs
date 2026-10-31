using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Finds standalone upscaler and frame-generation libraries.</summary>
/// <remarks>
///     An empty result means no standalone library was found, never that the game is unsupported:
///     many games link an upscaler statically.
/// </remarks>
internal static class UpscalerScan
{
    private static readonly (string Name, string Technology, string Family, string Kind)[] Known =
    [
        ("nvngx_dlss.dll", "DLSS", "dlss", "upscaler"),
        ("nvngx_dlssg.dll", "DLSS Frame Generation", "dlss", "frameGeneration"),
        ("nvngx_dlssd.dll", "DLSS Ray Reconstruction", "dlss", "other"),
        ("amd_fidelityfx_dx12.dll", "FSR 3.1", "fsr", "upscaler"),
        ("amd_fidelityfx_vk.dll", "FSR 3.1 (Vulkan)", "fsr", "upscaler"),
        ("amd_fidelityfx_loader_dx12.dll", "FidelityFX", "fsr", "upscaler"),
        ("amd_fidelityfx_loader_vk.dll", "FidelityFX (Vulkan)", "fsr", "upscaler"),
        ("amd_fidelityfx_upscaler_dx12.dll", "FSR upscaler", "fsr", "upscaler"),
        ("amd_fidelityfx_upscaler_vk.dll", "FSR upscaler (Vulkan)", "fsr", "upscaler"),
        ("amd_fidelityfx_framegeneration_dx12.dll", "FSR Frame Generation", "fsr", "frameGeneration"),
        ("amd_fidelityfx_framegeneration_vk.dll", "FSR Frame Generation (Vulkan)", "fsr", "frameGeneration"),
        ("ffx_fsr2_api_x64.dll", "FSR 2", "fsr", "upscaler"),
        ("ffx_fsr2_api_dx12_x64.dll", "FSR 2", "fsr", "upscaler"),
        ("ffx_fsr3upscaler_x64.dll", "FSR 3", "fsr", "upscaler"),
        ("libxess.dll", "XeSS", "xess", "upscaler"),
        ("libxess_dx11.dll", "XeSS (DirectX 11)", "xess", "upscaler"),
        ("libxess_fg.dll", "XeSS Frame Generation", "xess", "frameGeneration"),
        ("libxell.dll", "XeLL", "other", "other"),
        ("sl.interposer.dll", "Streamline", "other", "other"),
        ("sl.dlss.dll", "DLSS (Streamline)", "dlss", "upscaler"),
        ("sl.dlss_g.dll", "DLSS Frame Generation (Streamline)", "dlss", "frameGeneration")
    ];

    /// <summary>Lists recognised libraries, skipping files WSGM created itself.</summary>
    /// <param name="layout">The walked install folder, possibly from an earlier walk.</param>
    /// <param name="installed">Install-root-relative paths WSGM created, which are not the game's.</param>
    /// <returns>The libraries, by path; a walked file removed since is skipped.</returns>
    internal static IReadOnlyList<GameUpscalerFile> Scan(GameFolderLayout layout, IReadOnlySet<string> installed)
    {
        List<GameUpscalerFile> found = [];
        foreach (var file in layout.Libraries)
        {
            if (installed.Contains(file.RelativePath))
            {
                continue;
            }

            foreach (var known in Known)
            {
                if (file.Name.Equals(known.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(file.FullPath))
                    {
                        break;
                    }

                    var version = PeImage.TryVersion(file.FullPath);
                    var text = version is null ? null
                        : version.ProductVersion.Length > 0 ? version.ProductVersion
                        : version.FileVersion.Length > 0 ? version.FileVersion : null;
                    found.Add(new GameUpscalerFile(known.Technology, known.Family, known.Kind, file.RelativePath, text));
                    break;
                }
            }
        }

        return [.. found.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }
}
