using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One installed sound pack as the UI sounds page and the Customization hub show it.</summary>
/// <param name="Id">The library id, sent back with every command.</param>
/// <param name="Name">The name the user sees.</param>
/// <param name="Author">The manifest's author, or empty.</param>
/// <param name="Version">The manifest's version, or empty.</param>
/// <param name="Description">The manifest's description, or empty.</param>
/// <param name="StoreId">The DeckThemes listing it came from, or null for a local archive.</param>
/// <param name="Source">Repository or source text, or empty.</param>
/// <param name="Error">Why the pack cannot be used, or null.</param>
/// <param name="Compatibility">Its coverage of the current Steam resources, or null before a comparison.</param>
/// <param name="Mapped">The Steam resources it replaces.</param>
/// <param name="Defaults">The Steam resources that keep Steam's sound, missing or ignored.</param>
/// <param name="Problems">Missing, unsupported or unknown entries worth reading.</param>
/// <param name="Assets">The files it can preview, relative to the pack.</param>
internal sealed record SteamSoundsPack(
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    string? StoreId,
    string Source,
    string? Error,
    string? Compatibility,
    IReadOnlyList<string> Mapped,
    IReadOnlyList<string> Defaults,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Assets);
