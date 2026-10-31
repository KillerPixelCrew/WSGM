namespace WSGM.Core;

/// <summary>RTSS settings that are not per game. Per-game values live in <see cref="AppConfig.Profiles" />.</summary>
public sealed class PerformanceConfig
{
    /// <summary>Whether WSGM may observe and change supported RTSS profile properties.</summary>
    public bool Enabled { get; set; }

    /// <summary>How a frame limit relates to the panel's refresh rate.</summary>
    /// <remarks>
    ///     Global rather than per-application: it decides whether WSGM changes display modes at all,
    ///     which is a tolerance for mode-change risk the user holds once, not per game.
    /// </remarks>
    public FrameLimitStrategy FrameLimitStrategy { get; set; } = FrameLimitStrategy.FrameLimitOnly;

    /// <summary>
    ///     Custom overlay (level 4) widget order — HandheldCompanion's widget names,
    ///     comma-separated; unknown names are ignored.
    /// </summary>
    public string OsdCustomOrder { get; set; } = "Time,GPU,CPU,VRAM,RAM,BATT,FPS";

    /// <summary>Custom overlay clock detail: 0 hidden, 1 short time, 2 full timestamp.</summary>
    public int OsdCustomTime { get; set; } = 2;

    /// <summary>Custom overlay framerate detail: 0 hidden, 1 FPS, 2 FPS and frametime.</summary>
    public int OsdCustomFps { get; set; } = 2;

    /// <summary>Custom overlay CPU detail: 0 hidden, 1 load and power, 2 adds temperature.</summary>
    public int OsdCustomCpu { get; set; } = 2;

    /// <summary>Custom overlay memory detail: 0 hidden, 1 used, 2 used of total.</summary>
    public int OsdCustomRam { get; set; } = 2;

    /// <summary>Custom overlay GPU detail: 0 hidden, 1 load and power, 2 adds temperature.</summary>
    public int OsdCustomGpu { get; set; } = 2;

    /// <summary>Custom overlay video-memory detail: 0 hidden, 1 used, 2 used of total.</summary>
    public int OsdCustomVram { get; set; } = 2;

    /// <summary>
    ///     Custom overlay battery detail: 0 hidden, 1 percent and remaining time, 2 adds
    ///     the charge rate.
    /// </summary>
    public int OsdCustomBattery { get; set; } = 2;
}
