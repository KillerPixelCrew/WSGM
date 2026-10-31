namespace WSGM.Core;

/// <summary>A resolved value and the layer it came from.</summary>
/// <typeparam name="T">The nullable value type.</typeparam>
/// <param name="Value">The value, or null when nothing sets it.</param>
/// <param name="Source">The layer that supplied it.</param>
public readonly record struct Resolved<T>(T Value, ProfileSource Source)
{
    /// <summary>Whether the running game overrides this value.</summary>
    public bool IsGameOverride => Source is ProfileSource.Game;
}
