using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A user-defined command source with an explicit backing target.</summary>
public sealed class ManualShortcutConfig
{
    /// <summary>The durable user-authored source identity.</summary>
    public string Id { get; set; } = "";

    /// <summary>The title shown in Steam and import review.</summary>
    public string Name { get; set; } = "";

    /// <summary>The explicit executable path.</summary>
    public string Target { get; set; } = "";

    /// <summary>Verbatim arguments; an explicit content token may be resolved at launch.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>The authored working directory, or empty for the program directory.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>The distinct required backing file, or empty for the target itself.</summary>
    public string ContentPath { get; set; } = "";

    /// <summary>The friendly required library or storage label.</summary>
    public string Location { get; set; } = "";

    /// <summary>Whether this command participates in managed availability tracking.</summary>
    public bool LibraryBacked { get; set; } = true;

    /// <summary>The volume-bound file when this manual entry is a single ROM.</summary>
    public ManagedContentPath? RomPath { get; set; }

    /// <summary>The single ROM's canonical system, empty for an authored PC command.</summary>
    public string SystemId { get; set; } = "";

    /// <summary>The single ROM's selected installation, empty to follow the system preference.</summary>
    public string EmulatorInstallationId { get; set; } = "";

    /// <summary>The single ROM's explicit core selection.</summary>
    public string CoreId { get; set; } = "";

    /// <summary>The single ROM's typed argument override.</summary>
    public List<string> RomArguments { get; set; } = [];

    /// <summary>The accepted suffix override retained for this explicit single ROM.</summary>
    public List<string> RomExtensions { get; set; } = [];

    /// <summary>Copies this source without retaining mutable configuration ownership.</summary>
    public ManualShortcutConfig Copy()
    {
        var copy = (ManualShortcutConfig)MemberwiseClone();
        copy.RomPath = RomPath?.Copy();
        copy.RomArguments = [.. RomArguments];
        copy.RomExtensions = [.. RomExtensions];
        return copy;
    }
}
