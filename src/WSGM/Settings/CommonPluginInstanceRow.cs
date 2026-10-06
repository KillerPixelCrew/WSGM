using System;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Explicit activation preference for one installed plugin instance.</summary>
public sealed class CommonPluginInstanceRow : ObservableObject
{
    private readonly Func<bool> _cefAcknowledged;
    private string? _cefFailure;
    private bool _enabled;
    private bool _reloadRequested;
    private string? _savedCefFailure;
    private bool _savedEnabled;
    private bool _savedSteamCefEnabled;
    private bool _steamCefEnabled;

    internal CommonPluginInstanceRow(string pluginId, string instanceId, string name, bool enabled, bool installed,
        bool steamCef = false, bool steamCefEnabled = false, string? cefFailure = null,
        Func<bool>? cefAcknowledged = null)
    {
        PluginId = pluginId;
        InstanceId = instanceId;
        Name = name;
        Installed = installed;
        _enabled = _savedEnabled = enabled;
        SteamCef = steamCef;
        _steamCefEnabled = _savedSteamCefEnabled = steamCefEnabled;
        _cefFailure = _savedCefFailure = cefFailure;
        _cefAcknowledged = cefAcknowledged ?? (() => false);
    }

    /// <summary>Package identity.</summary>
    private string PluginId { get; }

    /// <summary>Stable instance identity.</summary>
    private string InstanceId { get; }

    /// <summary>Installed package display name.</summary>
    public string Name { get; }

    /// <summary>Whether the package declares unrestricted Steam CEF access.</summary>
    public bool SteamCef { get; }

    /// <summary>Whether the initial trust warning was acknowledged.</summary>
    public bool CanEnableSteamCef => SteamCef && _cefAcknowledged();

    /// <summary>User opt-in to this package's frontend modules.</summary>
    public bool SteamCefEnabled
    {
        get => _steamCefEnabled;
        set
        {
            if (value && !CanEnableSteamCef)
            {
                return;
            }

            _steamCefEnabled = value;
            Raise(nameof(SteamCefEnabled));
        }
    }

    /// <summary>The persisted failure naming the module and reason.</summary>
    public string CefFailure => _cefFailure ?? "";

    /// <summary>Whether this package was stopped after a frontend error.</summary>
    public bool HasCefFailure => !string.IsNullOrEmpty(_cefFailure);

    /// <summary>Explicitly retries a failed package when the user next saves.</summary>
    public RelayCommand ReloadCefCommand => field ??= new RelayCommand(() =>
    {
        if (!CanEnableSteamCef)
        {
            return;
        }

        _cefFailure = null;
        _reloadRequested = true;
        Enabled = true;
        SteamCefEnabled = true;
        Raise(nameof(CefFailure));
        Raise(nameof(HasCefFailure));
    });

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

    internal bool Edited => _enabled != _savedEnabled || _steamCefEnabled != _savedSteamCefEnabled
                                                      || _cefFailure != _savedCefFailure;

    internal void RefreshCefAcknowledgement()
    {
        Raise(nameof(CanEnableSteamCef));
    }

    internal CommonPluginInstanceConfig Capture()
    {
        return new CommonPluginInstanceConfig
        {
            PluginId = PluginId, InstanceId = InstanceId, Enabled = Enabled,
            SteamCefEnabled = SteamCefEnabled, SteamCefFailure = _cefFailure, SteamCefReloadRequested = _reloadRequested
        };
    }

    internal void AcceptSaved(CommonPluginInstanceConfig saved)
    {
        if (saved.PluginId == PluginId && saved.InstanceId == InstanceId)
        {
            _savedEnabled = saved.Enabled;
            _savedSteamCefEnabled = saved.SteamCefEnabled;
            _savedCefFailure = saved.SteamCefFailure;
            _reloadRequested = false;
        }
    }
}
