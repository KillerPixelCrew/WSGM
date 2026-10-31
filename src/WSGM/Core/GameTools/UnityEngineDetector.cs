using System.IO;

namespace WSGM.Core;

/// <summary>Unity: an executable beside <c>&lt;Name&gt;_Data</c> and <c>UnityPlayer.dll</c>.</summary>
internal sealed class UnityEngineDetector : IGameEngineDetector
{
    /// <inheritdoc />
    public void Detect(GameRuntimeEvidence evidence)
    {
        var layout = evidence.Layout;
        foreach (var file in layout.Executables)
        {
            var directory = file.Directory;
            var prefix = directory.Length == 0 ? "" : directory + "/";
            var data = prefix + Path.GetFileNameWithoutExtension(file.Name) + "_Data";
            if (!layout.HasDirectory(data) || !layout.HasFile(prefix + "UnityPlayer.dll"))
            {
                continue;
            }

            var label = layout.HasFile(prefix + "GameAssembly.dll") ? "Unity (IL2CPP)"
                : layout.HasDirectory(prefix + "MonoBleedingEdge") ? "Unity (Mono)"
                : "Unity";
            evidence.Engine = "unity";
            evidence.EngineLabel = label;
            evidence.Structural = true;
            evidence.Mark(file.RelativePath, 70, GameRuntimeRole.Game, "Unity player with its " +
                                                                        Path.GetFileName(data) + " folder");
        }
    }
}
