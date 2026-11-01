using System.Globalization;

namespace WSGM.Shell;

/// <summary>The bounds of a row the user sets with a slider.</summary>
/// <param name="Minimum">Inclusive lower bound.</param>
/// <param name="Maximum">Inclusive upper bound.</param>
/// <param name="Step">Movement per pad nudge; at least 1.</param>
/// <param name="Unit">The unit the value label names, such as FPS.</param>
/// <param name="OffBelow">
///     Lowest nonzero accepted value, or zero when every position is valid. Lower slider positions
///     read and commit as zero (off), preserving the same value domain as the Steam projection.
/// </param>
internal readonly record struct DescriptorRange(int Minimum, int Maximum, int Step, string Unit, int OffBelow = 0)
{
    /// <summary>The value as the slider's label reads it.</summary>
    /// <param name="value">A slider position.</param>
    /// <returns>"Off" for zero and every position below <see cref="OffBelow" />, otherwise the value and unit.</returns>
    internal string Label(int value)
    {
        return value < OffBelow || value == 0
            ? "Off"
            : string.Create(CultureInfo.InvariantCulture, $"{value} {Unit}");
    }
}
