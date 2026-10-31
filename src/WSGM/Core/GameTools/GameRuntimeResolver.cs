using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace WSGM.Core;

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
