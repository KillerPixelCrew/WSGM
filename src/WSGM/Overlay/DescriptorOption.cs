namespace WSGM.Overlay;

/// <summary>One named value of a row the user picks from a dropdown.</summary>
/// <param name="Value">The value written when it is chosen.</param>
/// <param name="Label">What the user reads.</param>
internal sealed record DescriptorOption(int Value, string Label);
