namespace WSGM.Shell;

/// <summary>One notebook.</summary>
/// <param name="Scope">Its identity.</param>
/// <param name="Title">The game's name, or "Global".</param>
/// <param name="Pages">How many pages it has.</param>
internal sealed record NotebookSummary(string Scope, string Title, int Pages);
