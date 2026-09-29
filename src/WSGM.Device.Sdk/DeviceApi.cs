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
    ///     <para>
    ///         Version 7 adds <c>OemControlDescriptor.CompanionApplication</c>, which marks the
    ///         manufacturer's companion-application button so the host can give it a default.
    ///     </para>
    ///     <para>
    ///         Version 8 adds <c>IDevicePlugin.SetMotionDemandAsync</c> and
    ///         <c>PluginMotionDemandContext</c>, the host's signal that nothing reads motion. The member
    ///         has a default implementation, so a plugin written against version 7 compiles unchanged
    ///         and keeps streaming motion as before.
    ///     </para>
    ///     <para>
    ///         Version 9 replaces every lifecycle and command <c>DateTimeOffset</c> deadline with
    ///         <c>Deadline</c>, measured on <c>ActiveClock</c>, which does not count time the process spent
    ///         frozen by Modern Standby. A wall-clock deadline expired during the freeze and failed work
    ///         that was mid-flight when the machine slept.
    ///     </para>
    ///     <para>
    ///         Version 10 simplifies the controller model. Controller samples, OEM events and haptic
    ///         frames carry no generation, sequence or quality; controller management carries no
    ///         generation; <c>ReleaseControllerAsync</c> is best effort and returns nothing, so the
    ///         handoff step and result types are gone; <c>SetMotionDemandAsync</c> is removed and a
    ///         plugin streams motion for as long as it owns the controller. The shared helpers in
    ///         <c>WSGM.Device.Sdk.Windows</c> and <c>WSGM.Device.Sdk.Input</c> are new.
    ///     </para>
    ///     <para>
    ///         Version 11 adds <c>CapabilityDescriptor.ProfileScope</c> and <c>ApplyTiming</c>, so a
    ///         publisher can say that a value is global only, that its driver keeps per-application
    ///         values itself, or that it holds only after a game or system restart. Both default to the
    ///         earlier behaviour.
    ///     </para>
    /// </remarks>
    public const int Version = 11;
}
