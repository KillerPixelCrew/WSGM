using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSGM.Core;

/// <summary>Static graphics API evidence from the chosen executable and its paired Unity runtime.</summary>
internal static class GameGraphicsApi
{
    /// <summary>Reads native imports once and, without a Direct3D import, recognizes Direct3D loaded by name.</summary>
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
        List<string> modules = [executable];
        foreach (var module in Directory.EnumerateFiles(folder, "*.dll").Where(path =>
                     Path.GetFileName(path).Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).Equals("engine.dll", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).Contains("RHI", StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(path).StartsWith("CryRender", StringComparison.OrdinalIgnoreCase)))
        {
            PeImage.VisitImports(module, Visit);
            modules.Add(module);
        }

        if (!found.Overlaps(AnyDirect3D))
        {
            AddDirect3DLoadedByName(found, modules, folder);
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

    private const string Direct3D8 = "Direct3D 8";
    private const string OpenGl = "OpenGL";

    private static readonly string[] AnyDirect3D = [Direct3D8, "Direct3D 9", "Direct3D 10", "Direct3D 11", "Direct3D 12"];

    // What a program that loads a runtime itself has to name: the entry point it asks for, and for
    // Direct3D 8, whose games often wrap it, also the library.
    private static readonly (string Api, byte[][] Markers)[] LoadedByName =
    [
        (Direct3D8, ["Direct3DCreate8"u8.ToArray(), "d3d8.dll"u8.ToArray(), "D3D8.dll"u8.ToArray(), "D3D8.DLL"u8.ToArray()]),
        ("Direct3D 9", ["Direct3DCreate9"u8.ToArray()]),
        ("Direct3D 11", ["D3D11CreateDevice"u8.ToArray()]),
        ("Direct3D 12", ["D3D12CreateDevice"u8.ToArray()])
    ];

    // Direct3D is often absent from the import table: the program loads it by name, or a renderer DLL beside
    // the program imports it, while opengl32.dll stays imported as a fallback. So OpenGL alone proves little
    // and gives way to a named Direct3D. A program that imports Direct3D never reaches this, because a probe
    // for another runtime inside it says nothing about what it renders with.
    private static void AddDirect3DLoadedByName(HashSet<string> found, IEnumerable<string> modules, string folder)
    {
        var named = NamedRuntimes(modules);
        if (named.Count == 0 && RendererImportsDirect3D8(folder))
        {
            named.Add(Direct3D8);
        }

        if (named.Count > 0)
        {
            found.Remove(OpenGl);
            found.UnionWith(named);
        }
    }

    private static bool RendererImportsDirect3D8(string folder)
    {
        var imported = false;
        foreach (var library in Directory.EnumerateFiles(folder, "*.dll").Where(path =>
                     !Path.GetFileName(path).Equals("d3d8.dll", StringComparison.OrdinalIgnoreCase)))
        {
            PeImage.VisitImports(library, name => imported |= Classify(name) == Direct3D8);
        }

        return imported;
    }

    // Streams each file, since a monolithic engine program can be larger than one array may be.
    private static HashSet<string> NamedRuntimes(IEnumerable<string> modules)
    {
        HashSet<string> named = new(StringComparer.Ordinal);
        var overlap = LoadedByName.SelectMany(api => api.Markers).Max(marker => marker.Length) - 1;
        var buffer = new byte[(1 << 20) + overlap];
        foreach (var module in modules)
        {
            try
            {
                using var stream = new FileStream(module, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var kept = 0;
                int read;
                while ((read = stream.Read(buffer, kept, buffer.Length - kept)) > 0)
                {
                    var window = buffer.AsSpan(0, kept + read);
                    foreach (var (api, markers) in LoadedByName)
                    {
                        foreach (var marker in markers)
                        {
                            if (window.IndexOf(marker) >= 0)
                            {
                                named.Add(api);
                            }
                        }
                    }

                    kept = Math.Min(overlap, window.Length);
                    window[^kept..].CopyTo(buffer);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable module is absent evidence, like unreadable import metadata.
            }
        }

        return named;
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
