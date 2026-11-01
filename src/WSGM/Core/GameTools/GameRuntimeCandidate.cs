using System.Collections.Generic;

namespace WSGM.Core;

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
