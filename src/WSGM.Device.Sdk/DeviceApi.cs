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
    ///         <c>IPluginHostAdapter.TraceChange</c>.
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
    ///         Version 11 adds the service scaffolding both first-party packages shared as copies:
    ///         <c>WSGM.Device.Sdk.Services</c> (<c>DeviceService</c>, <c>DeviceServiceLifecycle</c>, the
    ///         <c>DeviceCommandSerializer</c> with its observation loop and the generic
    ///         <c>DeviceRecoveryJournal</c>), <c>CommandResults</c> and <c>DiagnosticText</c>. A
    ///         plugin built against them cannot load on a host without them, so the exact match refuses it.
    ///         <c>DeviceDiagnosticsSnapshot</c>, which no host assembled, is removed. <c>HidDevices</c> gains
    ///         <c>EnumerateAll</c> and <c>Inspect</c> (<c>HidCollectionDetails</c>, <c>HidCapability</c>,
    ///         <c>HidReportType</c>) and <c>HidCollection.ReleaseNumber</c>, so Device Lab reads HID through the
    ///         same layer as the packages. <c>HapticOutputFrame</c> becomes a readonly record struct, so rumble
    ///         allocates nothing per frame. Identifiers, labels, collections and manifest fields are checked for
    ///         shape only: the length and count constants are removed, <c>PlainText.IsIdentifier</c> takes no
    ///         length, <c>ManifestValidationCode.LimitExceeded</c> becomes <c>InvalidText</c>, and
    ///         <c>PluginTrace.MaxMessageLength</c> is gone. <c>IPluginHostAdapter.TraceChange</c> loses its
    ///         default implementation, so every host adapter implements it.
    ///         <c>CapabilityCommand.ApplyPowerPair</c> is replaced by <c>PairedPowerLimitWatts</c>: the host
    ///         decides both limits of a declared power pair and every write to either carries the other, which
    ///         <c>DevicePowerPair.TryResolve</c> checks.
    ///     </para>
    /// </remarks>
    public const int Version = 11;
}
