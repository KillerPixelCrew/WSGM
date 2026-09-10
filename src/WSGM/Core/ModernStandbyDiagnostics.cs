using System;
using System.Collections.Generic;
using System.Globalization;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>What WSGM can honestly tell the user about the last standby.</summary>
/// <param name="Supported">Whether this machine does Modern Standby at all.</param>
/// <param name="Summary">One sentence for the settings surface.</param>
/// <param name="ArmedWakeSources">
/// The devices currently allowed to wake the machine, named as Windows names them. This is the one
/// genuinely actionable diagnostic here: Windows will not say what woke the machine, but it will
/// say what is permitted to, and on a handheld that list is usually the answer.
/// </param>
public sealed record ModernStandbyReport(
    bool Supported,
    string Summary,
    IReadOnlyList<string> ArmedWakeSources);

/// <summary>
/// Reads Windows' own account of the last standby for the settings surface.
/// </summary>
/// <remarks>
/// Deliberately reports only what Windows actually exposes. There is no documented call that says
/// what woke the machine, so this never names a culprit: it reports whether the resume was
/// attributed to a person, how long the machine slept, and how long it has been awake. Inventing a
/// cause from those would be a guess presented as a diagnosis, which is worse than saying less.
/// <para>
/// It also answers the "degrade safely" requirement: a machine without S0 low-power idle is told so
/// plainly, rather than being offered a feature that can never do anything for it.
/// </para>
/// </remarks>
public static class ModernStandbyDiagnostics
{
    /// <summary>Describes the last standby, or why nothing can be described.</summary>
    /// <returns>A report safe to show in settings; never throws.</returns>
    public static ModernStandbyReport Read()
    {
        try
        {
            ModernStandbySupport support = ModernStandby.Query();
            if (!support.LowPowerIdle)
            {
                return new ModernStandbyReport(
                    false,
                    "This machine does not report Modern Standby (S0 low-power idle), so this has nothing to act on.",
                    []);
            }

            IReadOnlyList<string> armed = ReadArmedWakeSources();
            StandbyTiming timing = ModernStandby.ReadStandbyTiming();
            if (timing.Slept <= TimeSpan.Zero)
            {
                return new ModernStandbyReport(
                    true, "This machine has not been in standby since it booted.", armed);
            }

            // Windows attributes the resume; it does not say what caused it, and neither does this.
            string attribution = ModernStandby.WasLastResumeUnattended()
                ? "Windows did not attribute the last wake to a person"
                : "Windows attributed the last wake to a person";
            return new ModernStandbyReport(
                true,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"Slept for {Describe(timing.Slept)}, awake for {Describe(timing.SinceWake)}. {attribution}."),
                armed);
        }
        catch (Exception ex)
        {
            Log.Warn($"Modern Standby diagnostics unavailable: {ex.Message}");
            return new ModernStandbyReport(false, "Windows did not report its standby state.", []);
        }
    }

    /// <summary>The devices Windows currently allows to wake the machine.</summary>
    /// <returns>Their names, or an empty list when the enumeration fails.</returns>
    /// <remarks>
    /// Bounded and best-effort: a diagnostic that could throw would take the whole settings page
    /// with it. Measured on the reference handheld on 2026-09-10, two of three wake-capable devices
    /// were armed — the Wi-Fi adapter and the USB4 root router — which is the shape of answer this
    /// is for.
    /// </remarks>
    private static IReadOnlyList<string> ReadArmedWakeSources()
    {
        try
        {
            List<string> armed = [];
            foreach (WakeDevice device in ModernStandby.EnumerateWakeDevices())
            {
                if (device.Armed && armed.Count < MaximumReportedWakeSources)
                {
                    armed.Add(device.Name);
                }
            }
            return armed;
        }
        catch (Exception ex)
        {
            Log.Warn($"Modern Standby wake sources unavailable: {ex.Message}");
            return [];
        }
    }

    /// <summary>Enough to diagnose a handheld; a list longer than this is not a settings row.</summary>
    private const int MaximumReportedWakeSources = 16;

    /// <summary>Renders a duration the way someone reading a settings page would say it.</summary>
    /// <param name="span">The duration to describe.</param>
    /// <returns>A short human-readable span.</returns>
    internal static string Describe(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{span.TotalSeconds:F0} seconds");
        }
        if (span.TotalHours < 1)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{span.TotalMinutes:F0} minutes");
        }
        return string.Create(CultureInfo.CurrentCulture, $"{span.TotalHours:F1} hours");
    }
}
