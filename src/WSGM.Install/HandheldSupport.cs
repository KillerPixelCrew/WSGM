using System.Collections.Generic;
using LibHandheld;
using LibHandheld.Contracts;
using Identity = LibHandheld.Contracts.DeviceIdentitySnapshot;

namespace WSGM.Install;

/// <summary>Native handheld support offered independently of installed plugin packages.</summary>
/// <param name="Definition">The exact supported model.</param>
/// <param name="Components">System components required by its native controller and motion sources.</param>
public sealed record HandheldOffer(HandheldDefinition Definition, IReadOnlyList<SetupComponent> Components);

/// <summary>Pure hardware matching for setup and application installation policy.</summary>
public static class HandheldSupport
{
    /// <summary>Finds the implemented native backend without loading or opening hardware.</summary>
    /// <param name="identity">Normalized machine observations.</param>
    /// <returns>The exact native support offer, or null.</returns>
    public static HandheldOffer? Detect(Identity identity)
    {
        var definition = HandheldDevice.Detect(identity);
        return definition is null ? null : new HandheldOffer(definition, SetupComponents.Required(definition));
    }
}
