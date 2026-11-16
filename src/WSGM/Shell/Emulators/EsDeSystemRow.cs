namespace WSGM.Shell;

/// <summary>One system the ROM folders hold, as the ES-DE page lists it.</summary>
/// <param name="SystemId">The canonical WSGM system.</param>
/// <param name="Name">The system's name as ES-DE shows it.</param>
/// <param name="Detail">"DuckStation, and 2 more in ES-DE · 2 folders", or what holds it back.</param>
/// <param name="Problem">Why ES-DE does not show it, or empty when it does.</param>
internal sealed record EsDeSystemRow(string SystemId, string Name, string Detail, string Problem);
