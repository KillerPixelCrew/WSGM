using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Identifies binaries from publisher resources and exports, never from a proxy filename alone.</summary>
/// <remarks>
///     Publisher/export markers are declared in the catalog and checked against official packages.
///     https://github.com/doitsujin/dxvk#dll-dependencies
///     https://github.com/SpecialKO/SpecialK/blob/main/SpecialK.def
///     https://dege.freeweb.hu/dgVoodoo2/ReadmeDirectX/
///     https://github.com/crosire/reshade/blob/main/source/dll_main.cpp (ReShadeVersion)
///     Identification is evidence, not ownership; only the install/adoption journal authorizes removal.
/// </remarks>
internal static class GameToolBinaryIdentification
{
    /// <summary>Reads all DLLs beside the selected runtime once for a game scan.</summary>
    internal static GameToolBinary[] Scan(string directory, string? executable = null)
    {
        var game = executable is null ? null : PeImage.TryVersion(executable);
        return [.. Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase).Select(path => Read(path, game))];
    }

    /// <summary>Reads one file without executing it.</summary>
    internal static GameToolBinary Read(string path, PeVersionInfo? game = null)
    {
        return Classify(Path.GetFileName(path), PeImage.TryArchitecture(path) ?? "", PeImage.TryVersion(path),
            PeImage.Exports(path), game);
    }

    /// <summary>Classifies detached PE evidence, also used by synthetic fixtures.</summary>
    internal static GameToolBinary Classify(string name, string architecture, PeVersionInfo? version,
        IReadOnlySet<string> exports, PeVersionInfo? game = null)
    {
        var matches = GameToolCatalog.Current.Tools.Where(tool => Matches(tool, version, exports)).ToArray();
        if (matches.Length == 1)
        {
            var tool = matches[0];
            var role = tool.Identity.UniversalProxy ? name : version?.OriginalFilename ?? "";
            var pipelines = tool.InstallModes.Where(mode => mode.Proxy.Equals(role, StringComparison.OrdinalIgnoreCase))
                .SelectMany(mode => mode.Apis).Distinct(StringComparer.Ordinal).ToArray();
            if (pipelines.Length == 0)
            {
                pipelines = ExportPipelines(exports, role);
            }

            return new GameToolBinary(name, tool.Id, tool.Name, architecture, pipelines,
                tool.Identity.ResourceVersion ? version?.ProductVersion ?? "" : "", "Publisher and export identity");
        }

        var publisher = version is not null && game is not null && game.CompanyName.Length > 0
                        && game.ProductName.Length > 0 && version.CompanyName == game.CompanyName
                        && version.ProductName == game.ProductName;
        return new GameToolBinary(name, publisher ? "game" : "", publisher ? "Game publisher library" : "Unknown DLL",
            architecture, ExportPipelines(exports, version?.OriginalFilename ?? ""), version?.ProductVersion ?? "",
            publisher ? "Product and publisher match the game; ownership is unproven" : "No unique tool identity");
    }

    private static bool Matches(GameToolDefinition tool, PeVersionInfo? info, IReadOnlySet<string> exports)
    {
        var identity = tool.Identity;
        if (info is not null && identity.VetoTokens.Any(info.Mentions))
        {
            return false;
        }

        return identity.UniqueExportGroups.Any(group => group.Length > 0 && group.All(exports.Contains))
               || (info is not null && GameToolIdentityCheck.IsTool(tool, info)
                   && (identity.ExportGroups.Length == 0
                       || identity.ExportGroups.Any(group => group.All(exports.Contains))));
    }

    private static string[] ExportPipelines(IReadOnlySet<string> exports, string original)
    {
        List<string> result = [];
        if (exports.Contains("DirectDrawCreate") || original.Equals("D3DImm.dll", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("DirectDraw / Direct3D 1–7");
        }

        if (exports.Contains("Direct3DCreate8"))
        {
            result.Add("Direct3D 8");
        }
        if (exports.Contains("Direct3DCreate9"))
        {
            result.Add("Direct3D 9");
        }
        if (exports.Contains("D3D10CoreCreateDevice"))
        {
            result.Add("Direct3D 10");
        }
        if (exports.Contains("D3D11CreateDevice"))
        {
            result.Add("Direct3D 11");
        }
        if (exports.Contains("D3D12CreateDevice"))
        {
            result.Add("Direct3D 12");
        }
        if (exports.Contains("wglCreateContext"))
        {
            result.Add("OpenGL");
        }
        if (exports.Contains("vkGetInstanceProcAddr"))
        {
            result.Add("Vulkan");
        }
        return [.. result];
    }
}
