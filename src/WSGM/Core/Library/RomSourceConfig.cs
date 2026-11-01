using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A configured ROM library; its identity is independent of the current mount path.</summary>
public sealed class RomSourceConfig
{
    /// <summary>The source identity, retained when its root moves or remounts.</summary>
    public string Id { get; set; } = "";

    /// <summary>The friendly library and storage label.</summary>
    public string Name { get; set; } = "";

    /// <summary>The configured directory bound to its expected volume.</summary>
    public ManagedContentPath Root { get; set; } = new() { Directory = true };

    /// <summary>The emulated system, independent of emulator choice.</summary>
    public string SystemId { get; set; } = "";

    /// <summary>The preferred installation for this source; empty follows the system preference.</summary>
    public string EmulatorInstallationId { get; set; } = "";

    /// <summary>The explicit core used by a RetroArch source.</summary>
    public string CoreId { get; set; } = "";

    /// <summary>Whether discovery visits subdirectories.</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Whether display names remove common bracketed filename annotations.</summary>
    public bool TitleCleanup { get; set; } = true;

    /// <summary>Accepted extensions; an empty list uses the system profile.</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>Filename or relative-path wildcard exclusions.</summary>
    public List<string> Exclusions { get; set; } = [];

    /// <summary>Typed launch argument override; empty follows the emulator definition.</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>Copies the source and its mutable matching rules.</summary>
    public RomSourceConfig Copy()
    {
        var copy = (RomSourceConfig)MemberwiseClone();
        copy.Root = Root.Copy();
        copy.Extensions = [.. Extensions];
        copy.Exclusions = [.. Exclusions];
        copy.Arguments = [.. Arguments];
        return copy;
    }

    /// <summary>Resolves the selected root on its expected volume's current mount.</summary>
    public string ResolveRoot()
    {
        return ManagedContentStorage.ResolvePath(Root);
    }
}
