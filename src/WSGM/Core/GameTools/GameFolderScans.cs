using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;

namespace WSGM.Core.GameTools;

/// <summary>A standalone upscaler or frame-generation library found in a game folder.</summary>
/// <param name="Technology">The technology shown to the user, such as <c>DLSS</c>.</param>
/// <param name="Family">The input family a tool may require: <c>dlss</c>, <c>fsr</c>, <c>xess</c> or <c>other</c>.</param>
/// <param name="Kind"><c>upscaler</c>, <c>frameGeneration</c> or <c>other</c>.</param>
/// <param name="RelativePath">The forward-slash path relative to the install root.</param>
/// <param name="Version">The file's product or file version, or null.</param>
internal sealed record GameUpscalerFile(string Technology, string Family, string Kind, string RelativePath,
    string? Version);

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
    /// <param name="layout">The walked install folder.</param>
    /// <param name="installed">Install-root-relative paths WSGM created, which are not the game's.</param>
    /// <returns>The libraries, by path.</returns>
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

/// <summary>Recognises installed anti-cheat systems from their files.</summary>
/// <remarks>
///     Heuristic: these markers are the common install layouts of each system, not a guarantee. A game
///     may ship an anti-cheat it does not enforce offline, or enforce one whose files live elsewhere. The
///     result only decides whether WSGM asks for an explicit acknowledgement.
/// </remarks>
internal static class AntiCheatScan
{
    private static readonly (string Pattern, string Name)[] DirectoryMarkers =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS*", "Easy Anti-Cheat"),
        ("BattlEye", "BattlEye"),
        ("AntiCheatExpert", "Anti-Cheat Expert"),
        ("ACE-*", "Anti-Cheat Expert"),
        ("EAAntiCheat*", "EA Anti-Cheat")
    ];

    private static readonly (string Pattern, string Name)[] FileMarkers =
    [
        ("start_protected_game.exe", "Easy Anti-Cheat"),
        ("EasyAntiCheat*.exe", "Easy Anti-Cheat"),
        ("EasyAntiCheat*.sys", "Easy Anti-Cheat"),
        ("BEService*.exe", "BattlEye"),
        ("*_BE.exe", "BattlEye"),
        ("EAAntiCheat*", "EA Anti-Cheat"),
        ("ACE-*", "Anti-Cheat Expert")
    ];

    /// <summary>Names the detected systems.</summary>
    /// <param name="layout">The walked install folder.</param>
    /// <returns>Distinct system names, sorted.</returns>
    internal static IReadOnlyList<string> Scan(GameFolderLayout layout)
    {
        SortedSet<string> found = new(StringComparer.Ordinal);
        foreach (var directory in layout.Directories)
        {
            var name = directory.Contains('/') ? directory[(directory.LastIndexOf('/') + 1)..] : directory;
            foreach (var marker in DirectoryMarkers.Where(marker =>
                         FileSystemName.MatchesSimpleExpression(marker.Pattern, name)))
            {
                found.Add(marker.Name);
            }
        }

        foreach (var file in layout.Executables.Concat(layout.Drivers).Concat(layout.Libraries))
        {
            foreach (var marker in FileMarkers.Where(marker =>
                         FileSystemName.MatchesSimpleExpression(marker.Pattern, file.Name)))
            {
                found.Add(marker.Name);
            }
        }

        return [.. found];
    }
}
