namespace WSGM.Device.Sdk;

/// <summary>Identifies the exact public plugin API supported by this build.</summary>
public static class DeviceApi
{
    /// <summary>Exact version required by WSGM, Device Lab, and every plugin.</summary>
    /// <remarks>
    ///     Version 2 added the overlay section vocabulary: <c>CapabilityDescriptorSet.Sections</c> and
    ///     the descriptor's <c>CategoryId</c>/<c>SortOrder</c> placement fields.
    ///     <para>
    ///         Version 3 added the suppressed diagnostic level and the repeat-suppressing trace:
    ///         <c>DeviceTraceLevel.Debug</c>, <c>PluginTrace.Debug</c>, <c>PluginTrace.Change</c> and
    ///         <c>IPluginHostAdapter.TraceChange</c>. The interface member has a default implementation, so
    ///         a host or test double written against version 2 still compiles and behaves as it did.
    ///     </para>
    ///     <para>
    ///         Version 4 changed <c>CanonicalControllerSample</c> and <c>MotionSample</c> to readonly
    ///         record structs so publishing each controller frame does not allocate contract objects.
    ///     </para>
    ///     <para>
    ///         Version 5 adds descriptor prominence and companion hints with
    ///         normal, unpaired defaults. The new descriptor setters require an exact API match so older
    ///         hosts reject incompatible plugin binaries before loading them.
    ///     </para>
    ///     <para>
    ///         Version 6 adds the manifest's <c>hardware</c>, <c>capabilities</c> and <c>wsgmVersion</c>
    ///         members. Hosts refuse a descriptor whose role the manifest does not declare, so a version 5
    ///         plugin would lose every capability; the exact match turns that into a clear refusal.
    ///     </para>
    /// </remarks>
    public const int Version = 6;
}
