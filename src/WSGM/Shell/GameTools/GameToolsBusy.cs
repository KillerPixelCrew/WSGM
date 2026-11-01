namespace WSGM.Shell;

/// <summary>The running operation.</summary>
/// <param name="AppId">The game it runs for.</param>
/// <param name="ToolId">The tool it changes.</param>
/// <param name="Operation">
///     <c>install</c>, <c>replaceExisting</c>, <c>update</c>, <c>switchChannel</c>, <c>repair</c>, <c>configure</c> or
///     <c>remove</c>.
/// </param>
/// <param name="Phase">
///     The stable phase id: <c>starting</c>, then for install, update, a channel switch and repair the four steps
///     <c>download</c>, <c>verify</c>, <c>copy</c> and <c>settings</c>; <c>settings</c> alone for configure,
///     <c>remove</c> for remove; <c>rescan</c> while the folder is read again afterwards.
/// </param>
/// <param name="Detail">The phase as one user-facing line, such as "Downloading OptiScaler 0.9.4 from GitHub".</param>
/// <param name="Bytes">Bytes downloaded, zero outside a download.</param>
/// <param name="Total">The expected download size, zero when unknown.</param>
/// <param name="Cancellable">Whether <c>cancel</c> can still stop it; false once game files are being written.</param>
/// <param name="Channel">The channel being installed, or null when the operation installs nothing.</param>
/// <param name="Version">The version being installed as the user reads it, or null until the release is known.</param>
internal sealed record GameToolsBusy(
    uint AppId,
    string ToolId,
    string Operation,
    string Phase,
    string Detail,
    long Bytes,
    long Total,
    bool Cancellable,
    string? Channel = null,
    string? Version = null);
