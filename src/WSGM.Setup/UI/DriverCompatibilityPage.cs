using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;
using WSGM.Setup.Engine;

namespace WSGM.Setup.UI;

/// <summary>Explicit setup opt-ins, independent of Full/Minimal and retained through Back navigation.</summary>
internal sealed class DriverCompatibilityPage : Page
{
    public DriverCompatibilityPage(WindowsProtectionState? state, string? error = null)
    {
        ReadError = error ?? string.Empty;
        Options =
        [
            new FeatureOption("defender", "Add WSGM Defender exclusions",
                "Excludes the installed WSGM folder and its hardware-helper processes from Microsoft Defender scanning. Existing exclusions stay in place.",
                false, null) { IsNew = true },
            new FeatureOption("memory", "Disable Memory Integrity",
                $"Current override: {Value(state?.MemoryIntegrity)}. Allows drivers blocked by HVCI; a restart is required."
                + (state?.MemoryIntegrityLocked == true ? " This setting is firmware-locked and cannot be changed here." : string.Empty),
                false, null) { IsNew = true, Enabled = state is { MemoryIntegrityLocked: false } },
            new FeatureOption("blocklist", "Disable vulnerable-driver blocking",
                $"Current override: {Value(state?.VulnerableDriverBlocklist)}. Allows drivers on Windows' vulnerable-driver blocklist; a restart is required. Certificate checks still apply.",
                false, null) { IsNew = true, Enabled = state is not null },
            new FeatureOption("cfg", "Disable system Control Flow Guard",
                $"Current override: {state?.ControlFlowGuard.ToString() ?? "Unavailable"}. Changes CFG for the whole system, leaving other exploit protections unchanged. Restart after installation.",
                false, null) { IsNew = true, Enabled = state is not null }
        ];
    }

    public override string Eyebrow => "Optional Windows settings";
    public override string Title => "Driver compatibility";
    public override string Lead => "These choices reduce Windows protection. Leave them off unless you want the change. "
                                   + "Only selected settings change when installation starts; WSGM restores its changes on uninstall.";
    public IReadOnlyList<FeatureOption> Options { get; }
    public string ReadError { get; }
    public bool HasReadError => ReadError.Length > 0;

    public DriverCompatibilityChoices Choices => new()
    {
        DefenderExclusions = Selected("defender"), DisableMemoryIntegrity = Selected("memory"),
        DisableDriverBlocklist = Selected("blocklist"), DisableControlFlowGuard = Selected("cfg")
    };

    private bool Selected(string key) => Options.Single(option => option.Key == key) is { On: true, Enabled: true };
    private static string Value(bool? value) => value is null ? "Windows default" : value.Value ? "On" : "Off";
}
