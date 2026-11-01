namespace WSGM.Shell;

/// <summary>AutoTDP's frame-rate target slider.</summary>
/// <param name="Fps">The target, within the bounds.</param>
/// <param name="MinimumFps">The lowest target.</param>
/// <param name="MaximumFps">The highest target.</param>
/// <param name="StatusText">The shared bounds line, or empty.</param>
/// <param name="Accent">Whether the running game's own profile supplies the target.</param>
internal sealed record AutoTdpRowTarget(int Fps, int MinimumFps, int MaximumFps, string StatusText, bool Accent);
