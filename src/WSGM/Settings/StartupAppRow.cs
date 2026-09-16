using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Editable settings for one program launched after the shell starts.</summary>
public sealed class StartupAppRow : ObservableObject
{
    /// <summary>Gets or sets the executable or protocol to launch.</summary>
    public string Path { get; set => SetField(ref field, value, nameof(Path)); } = "";

    /// <summary>Gets or sets the command-line arguments passed to the program.</summary>
    public string Args { get; set => SetField(ref field, value, nameof(Args)); } = "";

    /// <summary>Gets or sets whether this program participates in startup.</summary>
    public bool Enabled { get; set => SetField(ref field, value, nameof(Enabled)); } = true;

    /// <summary>Gets or sets whether the program needs an elevated launch.</summary>
    public bool Elevated { get; set => SetField(ref field, value, nameof(Elevated)); }

    /// <summary>Gets or sets whether the program is watched and restarted when it exits.</summary>
    public bool AutoRelaunch { get; set => SetField(ref field, value, nameof(AutoRelaunch)); }

}
