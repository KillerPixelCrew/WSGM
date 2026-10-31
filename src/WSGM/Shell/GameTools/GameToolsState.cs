using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>Everything the Game Tools page shows for one game, shared by the Overlay and Steam.</summary>
/// <param name="AppId">The Steam AppID or non-Steam shortcut id.</param>
/// <param name="Name">The game's name.</param>
/// <param name="IsShortcut">Whether the id is a non-Steam shortcut.</param>
/// <param name="InstallFolder">The game's current install folder, empty when Steam reports none.</param>
/// <param name="Availability"><c>ready</c>, <c>notInstalled</c>, <c>folderUnavailable</c>, <c>steamUnavailable</c> or <c>running</c>.</param>
/// <param name="Reason">A user-facing explanation when the game is not <c>ready</c>.</param>
/// <param name="Loading">Whether a scan is running and the other fields may be placeholders.</param>
/// <param name="EngineLabel">The detected engine shown to the user.</param>
/// <param name="Runtime">The chosen runtime executable, or null when the user must choose.</param>
/// <param name="Candidates">Every executable the game might run, best first.</param>
/// <param name="RememberedMissing">Whether the program the user confirmed earlier is gone.</param>
/// <param name="ScanTruncated">Whether the folder was too large to scan completely.</param>
/// <param name="Upscalers">Standalone upscaler and frame-generation libraries the game ships.</param>
/// <param name="AntiCheat">Names of anti-cheat systems recognised in the folder.</param>
/// <param name="AntiCheatWarning">
///     The finished anti-cheat warning naming what was found, or null when none was; installing then needs
///     <c>acknowledgeRisk: true</c>.
/// </param>
/// <param name="GpuVendors">Lowercase GPU vendors present: <c>amd</c>, <c>intel</c>, <c>nvidia</c>.</param>
/// <param name="Tools">Every catalog tool and its state for this game.</param>
/// <param name="Busy">The running operation, or null.</param>
/// <param name="Error">The last failure for this game, or null.</param>
/// <param name="Notice">The last success or information message for this game, or null.</param>
internal sealed record GameToolsState(
    uint AppId,
    string Name,
    bool IsShortcut,
    string InstallFolder,
    string Availability,
    string? Reason,
    bool Loading,
    string EngineLabel,
    GameToolsRuntime? Runtime,
    IReadOnlyList<GameToolsCandidate> Candidates,
    bool RememberedMissing,
    bool ScanTruncated,
    IReadOnlyList<GameToolsUpscaler> Upscalers,
    IReadOnlyList<string> AntiCheat,
    string? AntiCheatWarning,
    IReadOnlyList<string> GpuVendors,
    IReadOnlyList<GameToolsTool> Tools,
    GameToolsBusy? Busy,
    string? Error,
    string? Notice);
