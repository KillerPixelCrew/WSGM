namespace WSGM.Device.Sdk.Packaging;

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

    /// <summary>An identifier used a character outside the permitted set.</summary>
    InvalidIdentifier,

    /// <summary>A version string was not a dotted numeric version.</summary>
    InvalidVersion,

    /// <summary>A field exceeded its length or count limit.</summary>
    LimitExceeded,

    /// <summary>A path escaped the package directory, was absolute, or was rooted on a device.</summary>
    UnsafePath,

    /// <summary>The package was compiled against a different exact SDK API.</summary>
    InvalidApiVersion,
}

/// <summary>
/// One reason a manifest was rejected, anchored to the field that caused it.
/// </summary>
/// <param name="Path">Manifest field that caused the failure, for example <c>entryAssembly</c>.</param>
/// <param name="Code">Stable reason code.</param>
/// <param name="Message">Human-readable explanation for diagnostics.</param>
public sealed record ManifestValidationError(string Path, ManifestValidationCode Code, string Message);
