namespace WSGM.DeviceLab.Wizard;

/// <summary>One fan reading.</summary>
/// <param name="Name">Which fan.</param>
/// <param name="Value">The reading.</param>
/// <param name="Unit">Its unit, or <c>raw</c> when the unit is not known.</param>
internal sealed record LabFanReading(string Name, int Value, string Unit);
