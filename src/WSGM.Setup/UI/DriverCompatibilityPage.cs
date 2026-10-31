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
    /// <param name="kxDevice">The chosen handheld whose KX power path requires both driver settings off.</param>
    public DriverCompatibilityPage(WindowsProtectionState? state, string? error = null, string? kxDevice = null)
    {
        ReadError = error ?? string.Empty;
        KxDevice = kxDevice;
        var required = kxDevice is null ? string.Empty : $" Required for {kxDevice}'s KX power-limit path.";
        Options =
        [
            new FeatureOption("restore", "Create a Windows restore point",
                "Saves a System Restore point before setup changes anything, so this PC can be returned to its current state. Skipped when System Restore is off.",
                true, null) { IsNew = true },
            new FeatureOption("defender", "Add WSGM Defender exclusions",
                "Excludes the installed WSGM folder and the WSGM process from Microsoft Defender scanning. Existing exclusions stay in place.",
                false, null) { IsNew = true },
            new FeatureOption("memory", "Disable Memory Integrity",
                $"Current override: {Value(state?.MemoryIntegrity)}. Allows drivers blocked by HVCI; a restart is required."
                + (state?.MemoryIntegrityLocked == true ? " This setting is firmware-locked and cannot be changed here." : required),
                kxDevice is not null, null)
            {
                IsNew = true, Enabled = kxDevice is null && state is { MemoryIntegrityLocked: false }
            },
            new FeatureOption("blocklist", "Disable vulnerable-driver blocking",
                $"Current override: {Value(state?.VulnerableDriverBlocklist)}. Allows drivers on Windows' vulnerable-driver blocklist; a restart is required. Certificate checks still apply."
                + required,
                kxDevice is not null, null) { IsNew = true, Enabled = kxDevice is null && state is not null },
            new FeatureOption("cfg", "Disable system Control Flow Guard",
                $"Current override: {state?.ControlFlowGuard?.ToString() ?? "Unavailable"}. Changes CFG for the whole system, leaving other exploit protections unchanged. Restart after installation.",
                false, null) { IsNew = true, Enabled = state?.ControlFlowGuard is not null }
        ];
    }

    public override string Eyebrow => "Optional Windows settings";
    public override string Title => "Driver compatibility";
    public override string Lead => "A restore point is created first unless you switch it off. The other choices reduce Windows "
                                   + "protection; leave them off unless you want the change. Only selected settings change when "
                                   + "installation starts; WSGM restores its changes on uninstall.";
    public IReadOnlyList<FeatureOption> Options { get; }

    /// <summary>The handheld that made the driver settings mandatory, or null.</summary>
    public string? KxDevice { get; }
    public string ReadError { get; }
    public bool HasReadError => ReadError.Length > 0;

    public DriverCompatibilityChoices Choices => new()
    {
        CreateRestorePoint = Selected("restore"), DefenderExclusions = Selected("defender"),
        DisableMemoryIntegrity = KxDevice is not null || Selected("memory"),
        DisableDriverBlocklist = KxDevice is not null || Selected("blocklist"), DisableControlFlowGuard = Selected("cfg")
    };

    private bool Selected(string key) => Options.Single(option => option.Key == key) is { On: true, Enabled: true };
    private static string Value(bool? value) => value is null ? "Windows default" : value.Value ? "On" : "Off";
}
