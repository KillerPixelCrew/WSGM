namespace WSGM.Core;

/// <summary>A GOG play task that runs a file.</summary>
/// <param name="Path">The executable, relative to the install folder.</param>
/// <param name="WorkingDir">The working directory, relative to the install folder, or empty.</param>
/// <param name="Arguments">Its arguments, or empty.</param>
internal sealed record GogPlayTask(string Path, string WorkingDir, string Arguments);
