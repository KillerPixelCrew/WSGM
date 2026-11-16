namespace WSGM.Core;

/// <summary>One system the ROM folders hold, as ES-DE is given it, or the reason it is left out.</summary>
/// <param name="SystemId">The canonical WSGM system.</param>
/// <param name="Name">ES-DE's short name for the system, which is also its folder under the ROM folder; empty when ES-DE has none.</param>
/// <param name="FullName">The system's name as ES-DE shows it.</param>
/// <param name="Platform">ES-DE's scraper platform.</param>
/// <param name="Theme">ES-DE's theme name.</param>
/// <param name="Extensions">The file types ES-DE lists, each in lower and upper case.</param>
/// <param name="Commands">The launch commands, the System default first; empty when the system is left out.</param>
/// <param name="Folders">The ROM folders that hold the system's games.</param>
/// <param name="Problem">Why ES-DE does not show the system, or empty when it does.</param>
internal sealed record EsDeSystem(
    string SystemId,
    string Name,
    string FullName,
    string Platform,
    string Theme,
    string[] Extensions,
    EsDeCommand[] Commands,
    EsDeFolder[] Folders,
    string Problem)
{
    /// <summary>Whether ES-DE shows the system: it has a launch command and a connected folder.</summary>
    public bool Shown => Problem.Length == 0;
}
