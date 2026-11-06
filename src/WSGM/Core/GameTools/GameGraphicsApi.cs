using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace WSGM.Core;

/// <summary>Static graphics API evidence from the chosen executable and its paired Unity runtime.</summary>
internal static class GameGraphicsApi
{
    /// <summary>Reads imported graphics libraries; dynamically selected renderers remain a user choice.</summary>
    /// <param name="executable">The confirmed game executable.</param>
    /// <returns>Distinct API names, or an empty list when no reliable import was found.</returns>
    internal static string[] Detect(string executable)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        Read(executable);
        var unity = Path.Combine(Path.GetDirectoryName(executable)!, "UnityPlayer.dll");
        if (File.Exists(unity))
        {
            Read(unity);
        }

        return [.. found.Order(StringComparer.Ordinal)];

        void Read(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var imports = pe.PEHeaders.PEHeader?.ImportTableDirectory.RelativeVirtualAddress ?? 0;
                if (imports == 0)
                {
                    return;
                }

                var entries = pe.GetSectionData(imports).GetReader();
                for (var index = 0; index < 1024 && entries.RemainingBytes >= 20; index++)
                {
                    var lookup = entries.ReadUInt32();
                    var timestamp = entries.ReadUInt32();
                    var forwarder = entries.ReadUInt32();
                    var nameRva = entries.ReadInt32();
                    var address = entries.ReadUInt32();
                    if ((lookup | timestamp | forwarder | (uint)nameRva | address) == 0)
                    {
                        break;
                    }

                    var name = pe.GetSectionData(nameRva).GetReader();
                    var dll = new List<byte>();
                    while (name.RemainingBytes > 0 && dll.Count < 128 && name.ReadByte() is var value && value != 0)
                    {
                        dll.Add(value);
                    }

                    var api = System.Text.Encoding.ASCII.GetString(dll.ToArray()).ToLowerInvariant() switch
                    {
                        "d3d8.dll" => "Direct3D 8",
                        "d3d9.dll" => "Direct3D 9",
                        "d3d10.dll" or "d3d10_1.dll" or "d3d10core.dll" => "Direct3D 10",
                        "d3d11.dll" => "Direct3D 11",
                        "d3d12.dll" => "Direct3D 12",
                        "ddraw.dll" => "DirectDraw / Direct3D 1–7",
                        "opengl32.dll" => "OpenGL",
                        "vulkan-1.dll" => "Vulkan",
                        "glide.dll" => "Glide",
                        "glide2x.dll" => "Glide 2",
                        "glide3x.dll" => "Glide 3",
                        _ => null
                    };
                    if (api is not null)
                    {
                        found.Add(api);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                           or ArgumentOutOfRangeException)
            {
                // Missing evidence never turns into an invented renderer.
            }
        }
    }

    /// <summary>Suggests only a unique API match; an unknown or multi-renderer game keeps explicit choices.</summary>
    internal static string Suggest(GameToolDefinition tool, string executable, string architecture)
    {
        var evidence = Detect(executable);
        var modes = tool.Modes.Where(mode => mode.Architectures.Contains(architecture)
                                            && evidence.Contains(mode.Api, StringComparer.Ordinal)).ToArray();
        return modes.Length == 1 ? modes[0].Proxy : tool.Proxy.Default;
    }
}
