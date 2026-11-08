using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What reading the installed applications produced.</summary>
/// <param name="Programs">The applications, or empty when the read failed.</param>
/// <param name="Error">Why the read failed, or null.</param>
public sealed record InstalledProgramList(IReadOnlyList<InstalledProgram> Programs, string? Error = null);
