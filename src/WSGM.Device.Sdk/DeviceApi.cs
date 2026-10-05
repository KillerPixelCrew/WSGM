namespace WSGM.Device.Sdk;

/// <summary>Identifies the exact public plugin API supported by this build.</summary>
public static class DeviceApi
{
    /// <summary>Exact version required by WSGM, Device Lab, and every plugin.</summary>
    /// <remarks>
    ///     Version 12 removes the glyph count, length and SVG path caps (only the byte and decode bounds
    ///     remain), moves the host-only <c>CapabilityStateDelta</c> and <c>DeviceSections.IncludePredefined</c>
    ///     into WSGM, gives both manifest readers one rule set in <c>ManifestRules</c> (lowercase package
    ///     identifiers, a root entry assembly and a plain-text package name), and makes
    ///     <c>DeviceRecoveryJournal</c> no longer disposable. Its <c>BeginAsync</c> re-arms an unresolved entry
    ///     instead of refusing it, <c>CheckHealthAsync</c> and the value overload of
    ///     <c>CommandResults.Unverified</c> are removed, and <c>DeviceWriteBudget</c>,
    ///     <c>CapabilityValueValidation</c>, <c>CapabilityIds</c> and <c>SourceOwnership</c> are new. The full history is the API table in the SDK's
    ///     <c>docs/reference.md</c>.
    /// </remarks>
    public const int Version = 12;
}