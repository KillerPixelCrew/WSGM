using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Named choices and current selection for one host-owned setting.</summary>
/// <param name="Value">Selected machine value, or null for no selection.</param>
/// <param name="Choices">Ordered legal choices; UI sends the selected value without replaying intermediate options.</param>
internal sealed record DeviceHostSelection(string? Value, IReadOnlyList<CapabilityChoice> Choices);
