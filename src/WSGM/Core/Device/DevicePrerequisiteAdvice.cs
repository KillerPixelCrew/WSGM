namespace WSGM.Core;

/// <summary>What the user can do about an enabled handheld whose prerequisites are missing.</summary>
/// <param name="Detail">
///     One paragraph naming what is missing and what to do, or empty when nothing
///     is.
/// </param>
/// <param name="CanEnableIntegration">Whether offering to switch Device Integration on is useful.</param>
/// <param name="NeedsSetup">Whether the missing half can only be added by re-running setup.</param>
public sealed record DevicePrerequisiteAdvice(string Detail, bool CanEnableIntegration, bool NeedsSetup)
{
    /// <summary>Whether there is anything to tell the user.</summary>
    public bool HasAdvice => Detail.Length > 0;
}
