using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace WSGM.Core.GameTools;

/// <summary>How sure the resolver is about the runtime executable.</summary>
internal enum GameRuntimeConfidence
{
    /// <summary>The user confirmed it earlier and it is still present.</summary>
    Confirmed,

    /// <summary>An engine layout or Steam's shortcut target identifies it.</summary>
    Structural,

    /// <summary>It is the only plausible candidate, or decisively ahead of the others.</summary>
    Single,

    /// <summary>Several candidates are plausible; the user must choose.</summary>
    NeedsChoice
}

/// <summary>What a candidate executable most likely is.</summary>
internal enum GameRuntimeRole
{
    /// <summary>The program that renders the game.</summary>
    Game,

    /// <summary>A launcher, starter or settings program that starts the game.</summary>
    Launcher,

    /// <summary>A redistributable, installer, crash reporter or anti-cheat helper.</summary>
    Helper,

    /// <summary>Nothing identifies it either way.</summary>
    Unknown
}

/// <summary>One executable the game might run.</summary>
/// <param name="RelativePath">The forward-slash path relative to the install root, or absolute outside it.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Architecture"><c>x64</c>, <c>x86</c>, <c>arm64</c>, or empty when unknown.</param>
/// <param name="Role">What it most likely is.</param>
/// <param name="Reasons">User-facing evidence, strongest first.</param>
/// <param name="Score">The ranking score; higher is more likely the game.</param>
/// <param name="FullPath">The absolute path.</param>
internal sealed record GameRuntimeCandidate(
    string RelativePath,
    string FileName,
    string Architecture,
    GameRuntimeRole Role,
    IReadOnlyList<string> Reasons,
    int Score,
    string FullPath);

/// <summary>The resolver's answer.</summary>
/// <param name="Engine">The detected engine id.</param>
/// <param name="EngineLabel">The engine shown to the user.</param>
/// <param name="Candidates">All candidates, best first.</param>
/// <param name="Chosen">The chosen candidate, or null when the user must choose.</param>
/// <param name="Confidence">How sure the resolver is.</param>
/// <param name="RememberedMissing">Whether a remembered executable is gone and detection ran again.</param>
internal sealed record GameRuntimeResolution(
    string Engine,
    string EngineLabel,
    IReadOnlyList<GameRuntimeCandidate> Candidates,
    GameRuntimeCandidate? Chosen,
    GameRuntimeConfidence Confidence,
    bool RememberedMissing);

/// <summary>Evidence an engine detector adds to the shared candidate scoring.</summary>
internal sealed class GameRuntimeEvidence(GameFolderLayout layout)
{
    /// <summary>The folder being resolved.</summary>
    internal GameFolderLayout Layout { get; } = layout;

    /// <summary>The detected engine id, set by the detector that recognises the layout.</summary>
    internal string Engine { get; set; } = "unknown";

    /// <summary>The detected engine label.</summary>
    internal string EngineLabel { get; set; } = "Unknown engine";

    /// <summary>Score adjustments and reasons per relative path.</summary>
    internal Dictionary<string, (int Score, GameRuntimeRole? Role, List<string> Reasons)> Marks { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a detector placed the game structurally.</summary>
    internal bool Structural { get; set; }

    /// <summary>Adds evidence for one candidate.</summary>
    /// <param name="relativePath">The candidate.</param>
    /// <param name="score">The adjustment.</param>
    /// <param name="role">A role to assign, or null to leave it.</param>
    /// <param name="reason">The user-facing reason.</param>
    internal void Mark(string relativePath, int score, GameRuntimeRole? role, string reason)
    {
        if (!Marks.TryGetValue(relativePath, out var mark))
        {
            mark = (0, null, []);
        }

        mark.Score += score;
        mark.Role = role ?? mark.Role;
        mark.Reasons.Add(reason);
        Marks[relativePath] = mark;
    }
}

/// <summary>Recognises one engine's folder layout.</summary>
/// <remarks>Detectors only add evidence; adding one never changes installers.</remarks>
internal interface IGameEngineDetector
{
    /// <summary>Adds this engine's evidence when the layout matches.</summary>
    /// <param name="evidence">The shared evidence.</param>
    void Detect(GameRuntimeEvidence evidence);
}

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

/// <summary>Finds the executable a game actually runs inside its install folder.</summary>
/// <remarks>
///     Steam never exposes a store title's executable, so the resolver ranks every executable in the
///     folder from structural and naming evidence. It never picks "the first exe found": several
///     plausible candidates without a decisive winner produce <see cref="GameRuntimeConfidence.NeedsChoice" />.
///     Helper names are evidence, not hard exclusions, so an unusual game is still offered.
/// </remarks>
internal static class GameRuntimeResolver
{
    private const int DecisiveLead = 30;

    private static readonly IGameEngineDetector[] Detectors = [new UnrealEngineDetector(), new UnityEngineDetector()];

    // Folder-name prefixes: each matches the folder itself and any name that starts with it.
    private static readonly string[] HelperDirectories =
    [
        "_CommonRedist*", "Redist*", "Redistributables*", "DirectX*", "vcredist*", "__Installer*", "Installer*",
        "Support*", "EasyAntiCheat*", "BattlEye*", "CrashReporter*", "Prereqs*", "_Redist*"
    ];

    private static readonly string[] HelperNames =
    [
        "unins*", "*CrashHandler*", "CrashReportClient*", "*CrashReporter*", "UE*PrereqSetup*", "dxsetup*",
        "vc_redist*", "vcredist*", "EpicWebHelper*", "EasyAntiCheat*", "start_protected_game*", "BEService*",
        "*_BE.exe", "dotnet*", "*setup*.exe", "*installer*", "UnityCrashHandler*", "QtWebEngineProcess*",
        "cefsharp*", "*redist*", "DXSETUP*", "oalinst*", "PhysX*", "*touchup*", "7z*.exe"
    ];

    private static readonly string[] LauncherTokens =
        ["launcher", "bootstrap", "starter", "updater", "config", "setup", "settings", "launch"];

    /// <summary>Resolves the runtime executable.</summary>
    /// <param name="layout">The walked install folder.</param>
    /// <param name="directTarget">Steam's shortcut target, already seen through WSGM's wrapper, or null.</param>
    /// <param name="remembered">The remembered relative or absolute executable, or null.</param>
    /// <param name="gameName">The game's name, for name similarity.</param>
    /// <param name="architectures">Reuses architectures of unchanged files across scans, or null to read every file.</param>
    /// <returns>The resolution.</returns>
    internal static GameRuntimeResolution Resolve(GameFolderLayout layout, string? directTarget, string? remembered,
        string gameName, PeArchitectureCache? architectures = null)
    {
        var evidence = new GameRuntimeEvidence(layout);
        foreach (var detector in Detectors)
        {
            detector.Detect(evidence);
        }

        var name = Simplify(gameName);
        var largest = layout.Executables.Select(file => file.Size).DefaultIfEmpty(0).Max();
        List<GameRuntimeCandidate> candidates = [];
        foreach (var file in layout.Executables)
        {
            var architecture = architectures is null
                ? PeImage.TryArchitecture(file.FullPath)
                : architectures.Read(file);
            if (architecture is null)
            {
                continue;
            }

            evidence.Marks.TryGetValue(file.RelativePath, out var mark);
            List<string> reasons = [.. mark.Reasons ?? []];
            var score = mark.Score;
            var role = mark.Role ?? GameRuntimeRole.Unknown;
            if (file.Directory.Split('/').Any(part => HelperDirectories.Any(helper =>
                    FileSystemName.MatchesSimpleExpression(helper, part)))
                || HelperNames.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, file.Name)))
            {
                score -= 100;
                role = GameRuntimeRole.Helper;
                reasons.Add("Looks like an installer, redistributable, crash reporter or anti-cheat helper");
            }
            else if (LauncherTokens.Any(token => Path.GetFileNameWithoutExtension(file.Name)
                         .Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score -= 30;
                if (role == GameRuntimeRole.Unknown)
                {
                    role = GameRuntimeRole.Launcher;
                }

                reasons.Add("Its name suggests a launcher or settings program");
            }

            if (name.Length >= 3 && Simplify(Path.GetFileNameWithoutExtension(file.Name)) is { Length: >= 3 } simple
                                 && (simple.Contains(name, StringComparison.Ordinal) || name.Contains(simple, StringComparison.Ordinal)))
            {
                score += 15;
                reasons.Add("Named like the game");
            }

            if (largest > 0 && file.Size == largest && layout.Executables.Count > 1)
            {
                score += 10;
                reasons.Add("Largest program in the folder");
            }
            else if (largest > 0 && file.Size * 10 < largest && file.Size < 4 * 1024 * 1024)
            {
                score -= 10;
                reasons.Add("Much smaller than the largest program");
            }

            if (directTarget is not null && StoragePaths.Same(file.FullPath, directTarget))
            {
                score += 40;
                reasons.Add("The program Steam's shortcut starts");
            }

            if (role == GameRuntimeRole.Unknown && score > 0)
            {
                role = GameRuntimeRole.Game;
            }

            candidates.Add(new GameRuntimeCandidate(file.RelativePath, file.Name, architecture, role, reasons, score,
                file.FullPath));
        }

        // A shortcut can start a program outside its start folder; it is still the strongest candidate.
        if (directTarget is not null && File.Exists(directTarget)
                                     && candidates.All(candidate => !StoragePaths.Same(candidate.FullPath, directTarget))
                                     && PeImage.TryArchitecture(directTarget) is { } targetArchitecture)
        {
            candidates.Add(new GameRuntimeCandidate(GameToolPaths.Relative(layout.Root, directTarget),
                Path.GetFileName(directTarget), targetArchitecture, GameRuntimeRole.Game,
                ["The program Steam's shortcut starts"], 40, directTarget));
        }

        candidates = [.. candidates.OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)];
        var rememberedMissing = false;
        if (!string.IsNullOrEmpty(remembered))
        {
            var full = GameToolPaths.Resolve(layout.Root, remembered);
            var found = candidates.FirstOrDefault(candidate => StoragePaths.Same(candidate.FullPath, full));
            if (found is null && File.Exists(full) && PeImage.TryArchitecture(full) is { } rememberedArchitecture)
            {
                found = new GameRuntimeCandidate(GameToolPaths.Relative(layout.Root, full), Path.GetFileName(full),
                    rememberedArchitecture, GameRuntimeRole.Game, [], 0, full);
                candidates.Insert(0, found);
            }

            if (found is not null)
            {
                var confirmed = found with { Reasons = ["You confirmed this program", .. found.Reasons] };
                candidates[candidates.IndexOf(found)] = confirmed;
                return new GameRuntimeResolution(evidence.Engine, evidence.EngineLabel, candidates, confirmed,
                    GameRuntimeConfidence.Confirmed, false);
            }

            rememberedMissing = true;
        }

        var plausible = candidates.Where(candidate => candidate.Role is not GameRuntimeRole.Helper).ToArray();
        GameRuntimeCandidate? chosen = null;
        var confidence = GameRuntimeConfidence.NeedsChoice;
        if (plausible.Length == 1)
        {
            chosen = plausible[0];
            confidence = evidence.Structural && chosen.Role == GameRuntimeRole.Game
                ? GameRuntimeConfidence.Structural
                : GameRuntimeConfidence.Single;
        }
        else if (plausible.Length > 1 && plausible[0].Score - plausible[1].Score >= DecisiveLead)
        {
            chosen = plausible[0];
            var structuralWinner = evidence.Structural && chosen.Role == GameRuntimeRole.Game
                                   || directTarget is not null && StoragePaths.Same(chosen.FullPath, directTarget);
            confidence = structuralWinner ? GameRuntimeConfidence.Structural : GameRuntimeConfidence.Single;
        }

        return new GameRuntimeResolution(evidence.Engine, evidence.EngineLabel, candidates, chosen, confidence,
            rememberedMissing);
    }

    private static string Simplify(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}

/// <summary>Remembers the PE architecture of executables across scans while their size and time are unchanged.</summary>
/// <remarks>Each scan opens every executable of a game; a rescan of an unchanged folder then reads none.</remarks>
internal sealed class PeArchitectureCache
{
    private const int Capacity = 4096;

    private readonly Dictionary<string, (long Size, DateTime LastWriteUtc, string? Architecture)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();

    /// <summary>The architecture a program runs as, read from the file only when it changed.</summary>
    /// <param name="file">The walked file.</param>
    /// <returns>The architecture, or null when the file is not a readable PE image.</returns>
    internal string? Read(GameFolderFile file)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(file.FullPath, out var entry) && entry.Size == file.Size
                                                                  && entry.LastWriteUtc == file.LastWriteUtc)
            {
                return entry.Architecture;
            }
        }

        var architecture = PeImage.TryArchitecture(file.FullPath);
        lock (_sync)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.Clear();
            }

            _entries[file.FullPath] = (file.Size, file.LastWriteUtc, architecture);
        }

        return architecture;
    }
}
