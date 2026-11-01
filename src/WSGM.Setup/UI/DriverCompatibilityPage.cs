using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;
using WSGM.Setup.Engine;

namespace WSGM.Setup.UI;

/// <summary>Explicit setup opt-ins, independent of Full/Minimal and retained through Back navigation.</summary>
internal sealed class DriverCompatibilityPage : Page
{
    /// <param name="state">Current protection overrides, or null when they could not be read.</param>
    /// <param name="error">Why the state could not be read.</param>
    /// <param name="defenderExcluded">Whether Defender already excludes the WSGM folder and process.</param>
    /// <remarks>
    ///     A setting this PC already has, from an earlier setup run or set by hand, shows checked and locked as done;
    ///     setup then leaves it alone, and uninstall restores only what WSGM itself changed.
    /// </remarks>
    public DriverCompatibilityPage(WindowsProtectionState? state, string? error = null, bool defenderExcluded = false)
    {
        ReadError = error ?? string.Empty;
        var memoryOff = state?.MemoryIntegrity == false;
        var blocklistOff = state?.VulnerableDriverBlocklist == false;
        var cfgOff = state?.ControlFlowGuard == ControlFlowGuardSetting.Disabled;
        Options =
        [
            new FeatureOption("restore", "Create a Windows restore point",
                "Saves a System Restore point before setup changes anything, so this PC can be returned to its current state. Skipped when System Restore is off.",
                true, null) { IsNew = true },
            new FeatureOption("defender", "Add WSGM Defender exclusions",
                defenderExcluded
                    ? "Already set: Defender excludes the WSGM folder and the WSGM process."
                    : "Excludes the installed WSGM folder and the WSGM process from Microsoft Defender scanning. Existing exclusions stay in place.",
                defenderExcluded, null) { IsNew = true, Enabled = !defenderExcluded },
            new FeatureOption("memory", "Disable Memory Integrity",
                (memoryOff
                    ? "Already set: Memory Integrity is off on this PC."
                    : $"Current override: {Value(state?.MemoryIntegrity)}. Allows drivers blocked by HVCI; a restart is required.")
                + (state?.MemoryIntegrityLocked == true
                    ? " This setting is firmware-locked and cannot be changed here."
                    : string.Empty),
                memoryOff, null) { IsNew = true, Enabled = state is { MemoryIntegrityLocked: false } && !memoryOff },
            new FeatureOption("blocklist", "Disable vulnerable-driver blocking",
                blocklistOff
                    ? "Already set: vulnerable-driver blocking is off on this PC. Certificate checks still apply."
                    : $"Current override: {Value(state?.VulnerableDriverBlocklist)}. Allows drivers on Windows' vulnerable-driver blocklist; a restart is required. Certificate checks still apply.",
                blocklistOff, null) { IsNew = true, Enabled = state is not null && !blocklistOff },
            new FeatureOption("cfg", "Disable system Control Flow Guard",
                cfgOff
                    ? "Already set: system Control Flow Guard is off on this PC."
                    : $"Current override: {state?.ControlFlowGuard?.ToString() ?? "Unavailable"}. Changes CFG for the whole system, leaving other exploit protections unchanged. Restart after installation.",
                cfgOff, null) { IsNew = true, Enabled = state?.ControlFlowGuard is not null && !cfgOff }
        ];
    }

    public override string Eyebrow => "Optional Windows settings";
    public override string Title => "Driver compatibility";

    public override string Lead =>
        "A restore point is created first unless you switch it off. The other choices reduce Windows "
        + "protection; leave them off unless you want the change. Only selected settings change when "
        + "installation starts; WSGM restores its changes on uninstall.";

    public IReadOnlyList<FeatureOption> Options { get; }

    public string ReadError { get; }
    public bool HasReadError => ReadError.Length > 0;

    public DriverCompatibilityChoices Choices => new()
    {
        CreateRestorePoint = Selected("restore"), DefenderExclusions = Selected("defender"),
        DisableMemoryIntegrity = Selected("memory"),
        DisableDriverBlocklist = Selected("blocklist"),
        DisableControlFlowGuard = Selected("cfg")
    };

    private bool Selected(string key)
    {
        return Options.Single(option => option.Key == key) is { On: true, Enabled: true };
    }

    private static string Value(bool? value)
    {
        return value is null ? "Windows default" : value.Value ? "On" : "Off";
    }
}
