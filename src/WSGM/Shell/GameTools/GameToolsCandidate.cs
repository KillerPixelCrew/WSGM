using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One executable the user may choose.</summary>
/// <param name="RelativePath">Forward-slash path relative to the install folder, or absolute outside it.</param>
/// <param name="FileName">The file name.</param>
/// <param name="Architecture"><c>x64</c>, <c>x86</c>, <c>arm64</c> or empty.</param>
/// <param name="ArchitectureLabel">The architecture shown to the user, empty when unknown.</param>
/// <param name="Role"><c>game</c>, <c>launcher</c>, <c>helper</c> or <c>unknown</c>.</param>
/// <param name="RoleLabel">What the program most likely is, shown to the user.</param>
/// <param name="Reasons">Evidence, strongest first.</param>
/// <param name="Chosen">Whether it is the current runtime.</param>
internal sealed record GameToolsCandidate(
    string RelativePath,
    string FileName,
    string Architecture,
    string ArchitectureLabel,
    string Role,
    string RoleLabel,
    IReadOnlyList<string> Reasons,
    bool Chosen);
