namespace WSGM.Overlay;

/// <summary>The bounds of a row the user sets with a slider.</summary>
/// <param name="Minimum">Inclusive lower bound.</param>
/// <param name="Maximum">Inclusive upper bound.</param>
/// <param name="Step">Movement per pad nudge; at least 1.</param>
/// <param name="OffBelow">
///     Lowest nonzero accepted value, or zero when every position is valid. Lower slider positions
///     read and commit as zero (off), preserving the same value domain as the Steam projection.
/// </param>
internal readonly record struct DescriptorRange(int Minimum, int Maximum, int Step, int OffBelow = 0);
