using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One top-level registration as <see cref="SteamLibraryVdf.ReadRegistrations" /> reports it.</summary>
/// <param name="Path">The unescaped library path, or null when the block carries none.</param>
/// <param name="ContentId">The library's stable identity, or null when the block carries none.</param>
/// <param name="Label">The registration's Steam label, empty when it carries none.</param>
/// <param name="AppIds">The app ids of the block's <c>apps</c> list, in file order.</param>
public sealed record LibraryRegistration(
    string? Path,
    string? ContentId,
    string Label,
    IReadOnlyList<long> AppIds);
