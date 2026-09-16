using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Explicit activation preference for one installed plugin instance.</summary>
public sealed class CommonPluginInstanceRow : ObservableObject
{
    private bool _enabled;
    private bool _savedEnabled;

    internal CommonPluginInstanceRow(string pluginId, string instanceId, string name, bool enabled, bool installed)
    {
        PluginId = pluginId;
        InstanceId = instanceId;
        Name = name;
        Installed = installed;
        _enabled = _savedEnabled = enabled;
    }

    /// <summary>Package identity.</summary>
    private string PluginId { get; }

    /// <summary>Stable instance identity.</summary>
    private string InstanceId { get; }

    /// <summary>Installed package display name.</summary>
    public string Name { get; }

    /// <summary>Whether validated metadata and an entry assembly are installed.</summary>
    private bool Installed { get; }

    /// <summary>Instance information, including missing packages.</summary>
    public string Detail =>
        Installed ? $"{PluginId} / {InstanceId}" : $"{PluginId} / {InstanceId} (package unavailable)";

    /// <summary>Whether the resident host should activate this instance after Save.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Raise(nameof(Enabled));
        }
    }

    internal bool Edited => _enabled != _savedEnabled;

    internal CommonPluginInstanceConfig Capture()
    {
        return new CommonPluginInstanceConfig { PluginId = PluginId, InstanceId = InstanceId, Enabled = Enabled };
    }

    internal void AcceptSaved()
    {
        _savedEnabled = _enabled;
    }
}
