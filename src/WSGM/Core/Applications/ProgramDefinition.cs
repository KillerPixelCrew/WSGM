using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>A durable, explicitly approved desktop application and its resolved UI identity.</summary>
public sealed record ProgramDefinition
{
    /// <summary>Stable identity, also used by the Quick access pin.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>User-facing name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Absolute executable path.</summary>
    public string Executable { get; set; } = "";
    /// <summary>Arguments passed verbatim to the executable.</summary>
    public string Arguments { get; set; } = "";
    /// <summary>Optional absolute working directory.</summary>
    public string WorkingDirectory { get; set; } = "";
    /// <summary>SHA-256 of the executable explicitly approved by the user.</summary>
    public string ExecutableHash { get; set; } = "";
    /// <summary>Optional UI executable selected explicitly from a running application window.</summary>
    public string UiExecutable { get; set; } = "";
    /// <summary>Approved UI executable SHA-256; a changed UI requires another explicit selection.</summary>
    public string UiExecutableHash { get; set; } = "";
    /// <summary>Optional custom presentation image, reserved for richer editors.</summary>
    public string IconPath { get; set; } = "";
    /// <summary>Explicit UAC activation; ordinary programs always use the least-privilege launcher.</summary>
    public bool Elevated { get; set; }
    /// <summary>Explicit opt-in to single-instance IPC activation when a matched process has no window.</summary>
    public bool ReactivateWithoutWindow { get; set; }

    /// <summary>Normalizes durable identities without deleting missing executables or approving replacements.</summary>
    internal static List<ProgramDefinition> Normalize(List<ProgramDefinition>? entries)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        return (entries ?? []).Where(entry => entry is not null).Select(entry =>
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString("N");
                ids.Add(entry.Id);
            }

            entry.Name ??= "Program";
            entry.Executable ??= "";
            entry.Arguments ??= "";
            entry.WorkingDirectory ??= "";
            entry.ExecutableHash ??= "";
            entry.UiExecutable ??= "";
            entry.UiExecutableHash ??= "";
            entry.IconPath ??= "";
            return entry;
        }).ToList();
    }
}
