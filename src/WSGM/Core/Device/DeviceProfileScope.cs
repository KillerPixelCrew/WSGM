using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The authored profiles of one exact built-in handheld definition.</summary>
public sealed class DeviceProfileScope
{
    /// <summary>Exact model definition the profiles were authored for.</summary>
    public string DeviceDefinitionId { get; set; } = string.Empty;

    /// <summary>Plain built-in family identity.</summary>
    public string FamilyId { get; set; } = string.Empty;

    /// <summary>Named fan curves and lighting profiles; selection belongs to the global/per-game profile.</summary>
    public List<DeviceAuthoredProfile> Profiles { get; set; } = [];
}
