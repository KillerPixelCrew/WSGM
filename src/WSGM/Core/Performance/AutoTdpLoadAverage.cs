namespace WSGM.Core;

/// <summary>A running mean of one processor's load, kept without allocating per window.</summary>
/// <remarks>The default value holds no reading; assigning <c>default</c> is how an owner resets it.</remarks>
internal struct AutoTdpLoadAverage
{
    private double _sum;
    private int _count;

    /// <summary>The mean so far, or null when no window carried a reading.</summary>
    internal readonly double? Mean => _count > 0 ? _sum / _count : null;

    /// <summary>Adds one window's reading, skipping an absent or unusable one.</summary>
    /// <param name="value">The window's load percent, or null when no sensor published one.</param>
    internal void Add(double? value)
    {
        if (value is not { } load || !double.IsFinite(load) || load < 0)
        {
            return;
        }

        _sum += load;
        _count++;
    }
}
