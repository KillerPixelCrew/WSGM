using System;
using System.Linq;

namespace WSGM.Core;

/// <summary>Unreal Engine: a root <c>Engine</c> folder and <c>&lt;Project&gt;/Binaries/Win64</c> executables.</summary>
internal sealed class UnrealEngineDetector : IGameEngineDetector
{
    /// <inheritdoc />
    public void Detect(GameRuntimeEvidence evidence)
    {
        var layout = evidence.Layout;
        if (!layout.HasDirectory("Engine"))
        {
            return;
        }

        var binaries = layout.Executables.Where(file =>
        {
            var parts = file.RelativePath.Split('/');
            return parts.Length == 4 && !parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase)
                                     && parts[1].Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                                     && (parts[2].Equals("Win64", StringComparison.OrdinalIgnoreCase)
                                         || parts[2].Equals("WinGDK", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        if (binaries.Length == 0)
        {
            return;
        }

        evidence.Engine = "unreal";
        evidence.EngineLabel = "Unreal Engine";
        evidence.Structural = true;
        foreach (var file in binaries)
        {
            var shipping = file.Name.Contains("-Shipping", StringComparison.OrdinalIgnoreCase);
            evidence.Mark(file.RelativePath, shipping ? 80 : 60, GameRuntimeRole.Game,
                shipping ? "Unreal Engine shipping build" : "Unreal Engine game binary");
        }

        foreach (var file in layout.Executables.Where(file => file.Directory.Length == 0))
        {
            evidence.Mark(file.RelativePath, -40, GameRuntimeRole.Launcher,
                "Starts the Unreal Engine binary in " + binaries[0].Directory);
        }

        foreach (var file in layout.Executables.Where(file =>
                     file.RelativePath.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Mark(file.RelativePath, -60, GameRuntimeRole.Helper, "Part of the Unreal Engine runtime");
        }
    }
}
