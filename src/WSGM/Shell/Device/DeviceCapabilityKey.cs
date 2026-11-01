namespace WSGM.Shell;

/// <summary>Stable dictionary key for one semantic capability instance.</summary>
/// <param name="CapabilityId">Semantic capability identifier, unique together with the instance identifier.</param>
/// <param name="InstanceId">Optional subdevice/zone identifier; null denotes the uninstanced capability.</param>
internal readonly record struct DeviceCapabilityKey(string CapabilityId, string? InstanceId)
{
    /// <summary>Formats the key for diagnostics, separating a nonempty instance with a hash.</summary>
    /// <returns>The capability identifier alone, or capability and instance identifiers joined by <c>#</c>.</returns>
    public override string ToString()
    {
        return InstanceId is { Length: > 0 }
            ? $"{CapabilityId}#{InstanceId}"
            : CapabilityId;
    }
}
