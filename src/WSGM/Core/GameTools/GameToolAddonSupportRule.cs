namespace WSGM.Core;

/// <summary>One way a publisher's source names the games an add-on supports.</summary>
/// <remarks>
///     OptiPatcher writes <c>CHECK_UE(mgsdelta)</c> for an Unreal game and <c>exeName == "gwt.exe"</c> for any
///     other, so its catalog entry has one rule for each, as OptiScaler's setup script reads them.
/// </remarks>
internal sealed record GameToolAddonSupportRule
{
    /// <summary>A pattern whose first group captures a name.</summary>
    public string Pattern { get; init; } = "";

    /// <summary>The executable names one match supports, with <c>{0}</c> standing for the captured name.</summary>
    public string[] Names { get; init; } = [];
}
