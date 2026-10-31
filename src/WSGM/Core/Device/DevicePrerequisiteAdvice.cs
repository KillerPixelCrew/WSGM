namespace WSGM.Core;

/// <summary>What the user can do about an enabled handheld whose prerequisites are missing.</summary>
/// <param name="Detail">
///     One paragraph naming what is missing and what to do, or empty when nothing
///     is.
/// </param>
public sealed record DevicePrerequisiteAdvice(string Detail)
{
    /// <summary>Whether there is anything to tell the user.</summary>
    public bool HasAdvice => Detail.Length > 0;
}
