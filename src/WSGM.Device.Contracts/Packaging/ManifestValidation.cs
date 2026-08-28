namespace WSGM.Device.Contracts.Packaging;

/// <summary>
/// Why a manifest was rejected. Codes are stable so tooling and diagnostics can match on them.
/// </summary>
public enum ManifestValidationCode
{
    /// <summary>The document exceeded <see cref="ManifestLimits.MaxDocumentBytes"/>.</summary>
    DocumentTooLarge,

    /// <summary>The document was not well-formed JSON, or nested deeper than allowed.</summary>
    MalformedDocument,

    /// <summary>The document contained a member this schema version does not define.</summary>
    UnknownMember,

    /// <summary>A required field was absent or empty.</summary>
    MissingField,

    /// <summary>The schema version is outside the range this build understands.</summary>
    UnsupportedSchemaVersion,

    /// <summary>An identifier used a character outside the permitted set.</summary>
    InvalidIdentifier,

    /// <summary>A version string was not a dotted numeric version.</summary>
    InvalidVersion,

    /// <summary>A field exceeded its length or count limit.</summary>
    LimitExceeded,

    /// <summary>Two entries in the same collection shared an identifier.</summary>
    DuplicateIdentifier,

    /// <summary>A path escaped the package directory, was absolute, or was rooted on a device.</summary>
    UnsafePath,

    /// <summary>A reference named an identifier that no entry in the manifest defines.</summary>
    UnresolvedReference,

    /// <summary>The declared API range is empty or inverted.</summary>
    InvalidApiRange,

    /// <summary>A device definition had no hard identity constraint.</summary>
    NoHardIdentityConstraint,

    /// <summary>Marketing text was used as a hard identity gate.</summary>
    MarketingNameAsHardGate,

    /// <summary>An observation's weight was outside the permitted range.</summary>
    InvalidObservationWeight,

    /// <summary>An observation that needs accepted values did not supply any.</summary>
    MissingObservationValues,

    /// <summary>Provenance requiring a recorded approval did not carry one.</summary>
    MissingApprovalReference,

    /// <summary>A hexadecimal identifier was not four uppercase hexadecimal digits.</summary>
    InvalidHexIdentifier,
}

/// <summary>
/// One reason a manifest was rejected, anchored to the field that caused it.
/// </summary>
/// <param name="Path">Dotted path of the offending field, for example <c>devices[0].identity[2]</c>.</param>
/// <param name="Code">Stable reason code.</param>
/// <param name="Message">Human-readable explanation for diagnostics.</param>
public sealed record ManifestValidationError(string Path, ManifestValidationCode Code, string Message);
