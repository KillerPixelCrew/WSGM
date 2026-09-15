using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Editable settings for one program launched after the shell starts.</summary>
public sealed class StartupAppRow : ObservableObject
{
    private string _path = "";
    private string _args = "";
    private bool _enabled = true;
    private bool _elevated;
    private bool _autoRelaunch;

    /// <summary>Gets or sets the executable or protocol to launch.</summary>
    public string Path { get => _path; set => SetField(ref _path, value, nameof(Path)); }

    /// <summary>Gets or sets the command-line arguments passed to the program.</summary>
    public string Args { get => _args; set => SetField(ref _args, value, nameof(Args)); }

    /// <summary>Gets or sets whether this program participates in startup.</summary>
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value, nameof(Enabled)); }

    /// <summary>Gets or sets whether the program needs an elevated launch.</summary>
    public bool Elevated { get => _elevated; set => SetField(ref _elevated, value, nameof(Elevated)); }

    /// <summary>Gets or sets whether the program is watched and restarted when it exits.</summary>
    public bool AutoRelaunch { get => _autoRelaunch; set => SetField(ref _autoRelaunch, value, nameof(AutoRelaunch)); }

}
