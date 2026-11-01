namespace WSGM.Shell;

/// <summary>The variable-refresh switch as the device capability reports it.</summary>
/// <param name="Available">Whether the device publishes a variable-refresh capability this session can drive.</param>
/// <param name="Enabled">Whether variable refresh is on.</param>
/// <param name="StatusText">One line describing the state, or why the switch is absent.</param>
/// <param name="Accent">Whether the running game's own profile supplies the value.</param>
internal sealed record VariableRefreshView(bool Available, bool Enabled, string StatusText, bool Accent);
