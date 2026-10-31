using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The result of one Notes edit.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Page">The saved page, or on conflict the current page to reload from.</param>
/// <param name="Error">Why it was not applied, in words a page shows.</param>
/// <param name="ElementId">The element or stroke the edit created, when it created one.</param>
internal sealed record NoteEditResult(NoteEditStatus Status, NotePage? Page, string? Error, string? ElementId = null)
{
    /// <summary>Whether the edit was saved.</summary>
    internal bool Succeeded => Status == NoteEditStatus.Applied;

    internal static NoteEditResult Refuse(string error)
    {
        return new NoteEditResult(NoteEditStatus.Refused, null, error);
    }

    /// <summary>The result as a bridge answer.</summary>
    /// <returns>Applied, or a refusal carrying the reason.</returns>
    internal SteamUiCommandResult ToCommandResult()
    {
        return Succeeded ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, Error);
    }
}
