namespace WSGM.Overlay;

/// <summary>Which of WSGM's composite power views currently have something to show.</summary>
/// <param name="PowerPresets">The AC and battery power assignments offer presets.</param>
/// <param name="WindowsPlan">Windows offers energy plans to choose from.</param>
/// <param name="ManualTdp">The device offers a manual power mode.</param>
/// <param name="ProcessorCores">The active Windows plan exposes hybrid core placement.</param>
/// <remarks>
///     The Windows views stay available with device integration off; the device views need it, which the
///     page description decides from the Device snapshot.
/// </remarks>
internal readonly record struct DeviceCoreControls(
    bool PowerPresets,
    bool WindowsPlan,
    bool ManualTdp,
    bool ProcessorCores);
