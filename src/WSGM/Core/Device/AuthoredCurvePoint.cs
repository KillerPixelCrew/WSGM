namespace WSGM.Core;

/// <summary>One authored curve point.</summary>
/// <remarks>
///     A mutable configuration class rather than the SDK's <c>CurvePoint</c> struct, matching every
///     other stored shape in this file: configuration is deserialized, normalized in place, and
///     re-serialized, while the SDK value is an immutable runtime contract.
/// </remarks>
public sealed class AuthoredCurvePoint
{
    /// <summary>The input, for a fan curve a temperature.</summary>
    public int Input { get; set; }

    /// <summary>The output, for a fan curve a duty percentage.</summary>
    public int Output { get; set; }
}
