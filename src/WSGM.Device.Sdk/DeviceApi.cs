namespace WSGM.Device.Sdk;

/// <summary>Identifies the exact public plugin API supported by this build.</summary>
public static class DeviceApi
{
    /// <summary>Exact version required by WSGM, Device Lab, and every plugin.</summary>
    /// <remarks>
    /// Version 2 added the overlay section vocabulary: <c>CapabilityDescriptorSet.Sections</c> and
    /// the descriptor's <c>CategoryId</c>/<c>SortOrder</c> placement fields.
    /// <para>
    /// Version 3 added the suppressed diagnostic level and the repeat-suppressing trace:
    /// <c>DeviceTraceLevel.Debug</c>, <c>PluginTrace.Debug</c>, <c>PluginTrace.Change</c> and
    /// <c>IPluginHostAdapter.TraceChange</c>. The interface member has a default implementation, so
    /// a host or test double written against version 2 still compiles and behaves as it did.
    /// </para>
    /// </remarks>
    public const int Version = 3;
}
