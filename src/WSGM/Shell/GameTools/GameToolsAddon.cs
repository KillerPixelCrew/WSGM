namespace WSGM.Shell;

/// <summary>One add-on of a tool for one game, such as OptiPatcher for OptiScaler.</summary>
/// <param name="Id">The add-on id.</param>
/// <param name="Name">The product name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Homepage">The upstream project page.</param>
/// <param name="License">The upstream license identifier.</param>
/// <param name="Support"><c>supported</c>, <c>unsupported</c> or <c>unknown</c> for the game's program.</param>
/// <param name="Installed">Whether it is installed with the tool.</param>
/// <param name="Recommended">Whether Install includes it unless the user turns it off.</param>
/// <param name="CanChange">Whether it can be added or removed now.</param>
/// <param name="Text">The status line.</param>
/// <param name="Note">A vendor note, or null.</param>
/// <param name="UpdateAvailable">Whether a newer build than the installed one is published.</param>
internal sealed record GameToolsAddon(
    string Id,
    string Name,
    string Description,
    string Homepage,
    string License,
    string Support,
    bool Installed,
    bool Recommended,
    bool CanChange,
    string Text,
    string? Note,
    bool UpdateAvailable);
