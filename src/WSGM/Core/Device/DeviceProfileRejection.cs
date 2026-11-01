namespace WSGM.Core;

/// <summary>Why an authored profile cannot be applied to the device.</summary>
public enum DeviceProfileRejection
{
    /// <summary>The profile is usable.</summary>
    None,

    /// <summary>The device does not publish the capability this profile authors.</summary>
    CapabilityAbsent,

    /// <summary>The capability exists but does not take a curve.</summary>
    NotACurve,

    /// <summary>The profile carries no points.</summary>
    PointCount,

    /// <summary>Inputs are not strictly ascending.</summary>
    NotAscending,

    /// <summary>A point sits outside the bounds the device declared.</summary>
    OutOfBounds
}
