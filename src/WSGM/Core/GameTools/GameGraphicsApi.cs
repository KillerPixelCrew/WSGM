using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSGM.Core;

/// <summary>Static graphics API evidence from the chosen executable and its paired Unity runtime.</summary>
internal static class GameGraphicsApi
{
    /// <summary>Reads native imports once; dynamically selected renderers remain a user choice.</summary>
    /// <param name="executable">The confirmed game executable.</param>
    /// <returns>Distinct canonical API names.</returns>
    internal static string[] Detect(string executable)
    {
        return Read(executable).Pipelines;
    }

    /// <summary>Reads the executable and known engine renderer modules, retaining loader evidence.</summary>
    internal static GameGraphicsEvidence Read(string executable)
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        HashSet<string> imports = new(StringComparer.OrdinalIgnoreCase);
        PeImage.VisitImports(executable, Visit);
        var folder = Path.GetDirectoryName(executable)!;
        foreach (var module in Directory.EnumerateFiles(folder, "*.dll").Where(path =>
                     Path.GetFileName(path).Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).Equals("engine.dll", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).Contains("RHI", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).StartsWith("CryRender", StringComparison.OrdinalIgnoreCase)))
        {
            PeImage.VisitImports(module, Visit);
        }

        return new GameGraphicsEvidence([.. found.Order(StringComparer.Ordinal)], [.. imports.Order(StringComparer.Ordinal)]);

        void Visit(ReadOnlySpan<byte> name)
        {
            if (Classify(name) is { } api)
            {
                found.Add(api);
            }

            foreach (var loader in LoaderNames)
            {
                if (Ascii.EqualsIgnoreCase(name, loader.Bytes))
                {
                    imports.Add(loader.Name);
                }
            }
        }
    }

    private static readonly (string Name, byte[] Bytes)[] LoaderNames =
    [
        ("dinput8.dll", "dinput8.dll"u8.ToArray()), ("d3d11.dll", "d3d11.dll"u8.ToArray()),
        ("dxgi.dll", "dxgi.dll"u8.ToArray()), ("nvapi.dll", "nvapi.dll"u8.ToArray()),
        ("nvapi64.dll", "nvapi64.dll"u8.ToArray()), ("amd_ags_x64.dll", "amd_ags_x64.dll"u8.ToArray())
    ];

    /// <summary>Classifies a borrowed ASCII import name without allocating.</summary>
    internal static string? Classify(ReadOnlySpan<byte> name)
    {
        if (Ascii.EqualsIgnoreCase(name, "d3d8.dll"u8))
        {
            return "Direct3D 8";
        }

        if (Ascii.EqualsIgnoreCase(name, "d3d9.dll"u8))
        {
            return "Direct3D 9";
        }

        if (Ascii.EqualsIgnoreCase(name, "d3d10.dll"u8) || Ascii.EqualsIgnoreCase(name, "d3d10_1.dll"u8)
            || Ascii.EqualsIgnoreCase(name, "d3d10core.dll"u8))
        {
            return "Direct3D 10";
        }

        if (Ascii.EqualsIgnoreCase(name, "d3d11.dll"u8))
        {
            return "Direct3D 11";
        }

        if (Ascii.EqualsIgnoreCase(name, "d3d12.dll"u8))
        {
            return "Direct3D 12";
        }

        if (Ascii.EqualsIgnoreCase(name, "ddraw.dll"u8) || Ascii.EqualsIgnoreCase(name, "d3dim.dll"u8)
            || Ascii.EqualsIgnoreCase(name, "d3dim700.dll"u8) || Ascii.EqualsIgnoreCase(name, "d3dimm.dll"u8)
            || Ascii.EqualsIgnoreCase(name, "d3drm.dll"u8))
        {
            return "DirectDraw / Direct3D 1–7";
        }

        if (Ascii.EqualsIgnoreCase(name, "opengl32.dll"u8))
        {
            return "OpenGL";
        }

        if (Ascii.EqualsIgnoreCase(name, "vulkan-1.dll"u8))
        {
            return "Vulkan";
        }

        if (Ascii.EqualsIgnoreCase(name, "glide.dll"u8))
        {
            return "Glide";
        }

        if (Ascii.EqualsIgnoreCase(name, "glide2x.dll"u8))
        {
            return "Glide 2";
        }

        if (Ascii.EqualsIgnoreCase(name, "glide3x.dll"u8))
        {
            return "Glide 3";
        }

        return null;
    }
}
