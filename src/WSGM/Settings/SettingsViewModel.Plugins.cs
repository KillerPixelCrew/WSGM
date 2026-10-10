using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibHandheld.Contracts;
using WSGM.Core;
using WSGM.Install;
using WSGM.Themes;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    private string _deviceProfileDefinition = string.Empty;
    private string _deviceProfileFamily = string.Empty;
    private bool _deviceProfilesEdited;
    private bool? _repairAvailable;
    private DeviceProfileRowViewModel? _selectedDeviceProfile;

    /// <summary>Installed common integrations and configured instances, independent of Device integration.</summary>
    public ObservableCollection<CommonPluginInstanceRow> CommonPlugins { get; } = [];

    /// <summary>The initial trust warning shown before enabling unrestricted frontends.</summary>
    public string SteamCefPluginWarning => SteamCefTrust.Warning;

    /// <summary>Explicit user acknowledgement of the initial Steam plugin warning.</summary>
    public bool SteamCefPluginWarningAccepted
    {
        get => field;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            Raise(nameof(SteamCefPluginWarningAccepted));
            foreach (var row in CommonPlugins)
            {
                row.RefreshCefAcknowledgement();
            }
        }
    }

    /// <summary>Metadata discovery failures; discovery never executes plugin code.</summary>
    public string CommonPluginDiscoveryError { get; private set; } = "";

    /// <summary>Package files in the Plugins folder.</summary>
    public ObservableCollection<PluginPackageRow> InstalledPackages { get; } = [];

    /// <summary>Plugins the installed release bundles that this machine can install.</summary>
    public ObservableCollection<PluginPackageRow> AvailablePackages { get; } = [];

    /// <summary>Bundled plugins for other hardware, and community plugins this release could not build.</summary>
    public ObservableCollection<PluginPackageRow> UnavailablePackages { get; } = [];

    /// <summary>Whether anything is installed.</summary>
    public bool HasInstalledPackages => InstalledPackages.Count > 0;

    /// <summary>Whether the release offers anything to install.</summary>
    public bool HasAvailablePackages => AvailablePackages.Count > 0;

    /// <summary>Whether the release bundles plugins this machine cannot use.</summary>
    public bool HasUnavailablePackages => UnavailablePackages.Count > 0;

    /// <summary>Whether the installed setup is present to run Repair.</summary>
    /// <remarks>Read once: setup does not appear or vanish while the window is open.</remarks>
    public bool CanRepair => _repairAvailable ??= _services.RepairAvailable();

    /// <summary>Runs the installed setup's repair, which installs what the plugins need.</summary>
    public RelayCommand RepairCommand => field ??= new RelayCommand(() => _services.StartRepair());

    /// <summary>Authored fan and lighting profiles for the installed device.</summary>
    public ObservableCollection<DeviceProfileRowViewModel> DeviceProfiles { get; } = [];

    /// <summary>Gets or sets the profile the curve editor is showing.</summary>
    public DeviceProfileRowViewModel? SelectedDeviceProfile
    {
        get => _selectedDeviceProfile;
        set
        {
            if (ReferenceEquals(_selectedDeviceProfile, value))
            {
                return;
            }

            if (_selectedDeviceProfile is not null)
            {
                _selectedDeviceProfile.PropertyChanged -= OnSelectedDeviceProfileChanged;
            }

            _selectedDeviceProfile = value;
            if (_selectedDeviceProfile is not null)
            {
                _selectedDeviceProfile.PropertyChanged += OnSelectedDeviceProfileChanged;
            }

            Raise(nameof(SelectedDeviceProfile));
            Raise(nameof(HasSelectedDeviceProfile));
        }
    }

    /// <summary>Whether a profile is selected and the editor has something to draw.</summary>
    public bool HasSelectedDeviceProfile => _selectedDeviceProfile is not null;

    /// <summary>Whether exact built-in device metadata permits authoring profiles.</summary>
    public bool DeviceProfilesAvailable => _deviceProfileDefinition.Length > 0;

    /// <summary>Explains an unavailable profile editor without activating device hardware.</summary>
    public string DeviceProfilesEmptyReason => InventoryDiscoveryText.Length > 0
        ? InventoryDiscoveryText
        : "No supported handheld was detected. Device profiles become available when this model is supported.";

    /// <summary>Starts the installed setup's repair, which installs what the plugins need.</summary>
    internal static void StartSetupRepair()
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstallLayout.SetupExe, "/repair") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Log.Warn("Plugins: starting setup's repair failed: " + ex.Message);
        }
    }

    /// <summary>
    ///     Reads the Plugins folder and the installed release's bundle on a worker, then fills the Plugins
    ///     page, the integration instances and the plugin settings of the installed device plugin.
    /// </summary>
    /// <returns>Completion after worker catalog reads and publication on the captured UI context.</returns>
    internal async Task LoadPluginPackagesAsync()
    {
        var page = await Task.Run(_services.ReadPackages);
        LoadPluginPackages(page);
        LoadCommonPlugins(page);
    }

    /// <summary>Fills the Plugins page: installed files, and what the installed release bundles.</summary>
    /// <param name="page">The page read on a worker.</param>
    private void LoadPluginPackages(PluginPackagePage page)
    {
        InstalledPackages.Clear();
        AvailablePackages.Clear();
        UnavailablePackages.Clear();
        foreach (var state in page.Rows)
        {
            PluginPackageRow row = new(state, action => _services.ActOnPackage(action, page.Bundle));
            (state.Section switch
            {
                PluginPackageSection.Installed => InstalledPackages,
                PluginPackageSection.Available => AvailablePackages,
                _ => UnavailablePackages
            }).Add(row);
        }
    }

    /// <summary>Reads the installed packages and what the installed release bundles for this machine.</summary>
    /// <returns>The catalog, the adapters, the bundle when it could be read, and the page's rows.</returns>
    /// <remarks>Opens and hashes every package and enumerates the display adapters: call it on a worker.</remarks>
    internal static PluginPackagePage ReadPluginPackagePage()
    {
        var catalog = PluginPackageCatalog.Discover(InstallLayout.Plugins);
        // One adapter read serves the available common-plugin offers.
        var adapters = CommonPluginEnablement.ReadAdapters();
        BundleManifest? bundle = null;
        PluginOffers? offers = null;
        try
        {
            bundle = BundleManifest.TryRead(InstallLayout.InstalledBundle);
            if (bundle is not null)
            {
                string[] installed =
                [
                    .. catalog.Common.Select(package => package.Manifest.Id)
                ];
                offers = PluginOffers.Compute(bundle, DeviceMachineIdentity.Collect(), adapters, installed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("Plugins: the installed bundle could not be read: " + ex.Message);
        }

        var rows = PluginPackageManager.Rows(catalog, bundle, InstallLayout.SetupPackages, offers,
            new PendingPluginRemovalStore(InstallLayout.PendingPluginRemovals));
        return new PluginPackagePage(catalog, adapters, bundle, rows.Select(row =>
        {
            if (string.IsNullOrEmpty(row.PackagePath))
            {
                return row;
            }

            try
            {
                using var package = PluginPackageFile.Open(row.PackagePath);
                return row with { SteamCef = package.CommonManifest?.SteamCef == true };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return row;
            }
        }).ToArray());
    }

    /// <summary>Installs or removes one package on a worker and returns the row's notice.</summary>
    /// <param name="row">The row whose action runs.</param>
    /// <param name="bundle">The installed release's bundle, which an install verifies against.</param>
    /// <returns>What the action did, or why it did not.</returns>
    internal static Task<string> ActOnPluginPackageAsync(PluginPackageRowState row, BundleManifest? bundle)
    {
        return Task.Run(() =>
        {
            PendingPluginRemovalStore removals = new(InstallLayout.PendingPluginRemovals);
            try
            {
                return row.Action switch
                {
                    PluginPackageAction.Install when bundle is not null => PluginPackageManager.Install(
                        row.PackagePath, bundle, InstallLayout.Plugins, removals),
                    PluginPackageAction.Remove => PluginPackageManager.Remove(row.PackagePath, InstallLayout.Plugins,
                        removals),
                    _ => ""
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Plugins: {row.Action} {row.Id} failed: {ex.Message}");
                return ex is UnauthorizedAccessException
                    ? "Changing the Plugins folder needs administrator rights."
                    : "That did not work: " + ex.Message;
            }
        });
    }

    private void LoadCommonPlugins(PluginPackagePage page)
    {
        var catalog = page.Catalog;
        SteamCefPluginWarningAccepted = _config.SteamCefPluginWarningAccepted;
        CommonPlugins.Clear();
        foreach (var package in catalog.Common)
        {
            var configured = _config.PluginInstances.Where(entry => entry.PluginId == package.Manifest.Id).ToArray();
            if (configured.Length == 0)
            {
                // Use the package's declared defaults and this page's adapter snapshot.
                CommonPlugins.Add(new CommonPluginInstanceRow(package.Manifest.Id,
                    CommonPluginEnablement.DefaultInstanceId, package.Manifest.Name,
                    CommonPluginEnablement.EnabledByDefault(package.Manifest, page.Adapters), true,
                    package.Manifest.SteamCef, cefAcknowledged: () => SteamCefPluginWarningAccepted));
            }

            foreach (var instance in configured)
            {
                CommonPlugins.Add(new CommonPluginInstanceRow(instance.PluginId, instance.InstanceId,
                    package.Manifest.Name, instance.Enabled, true, package.Manifest.SteamCef,
                    instance.SteamCefEnabled, instance.SteamCefFailure, () => SteamCefPluginWarningAccepted));
            }
        }

        foreach (var instance in _config.PluginInstances.Where(entry =>
                     catalog.Common.All(package => package.Manifest.Id != entry.PluginId)))
        {
            CommonPlugins.Add(new CommonPluginInstanceRow(instance.PluginId, instance.InstanceId, instance.PluginId,
                instance.Enabled, false));
        }

        CommonPluginDiscoveryError = string.Join(Environment.NewLine, catalog.Errors);
        Raise(nameof(CommonPluginDiscoveryError));
    }

    private void OnSelectedDeviceProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        _deviceProfilesEdited = true;
    }

    /// <summary>Adds an empty fan curve the user can then shape.</summary>
    /// <param name="capabilityId">The capability the new profile authors.</param>
    /// <param name="color">Whether to author a colour rather than a curve.</param>
    /// <remarks>
    ///     Seeded with two points at the ends rather than none. A curve needs at least two to be valid,
    ///     and an editor opening on an empty plot gives the user nothing to grab.
    /// </remarks>
    internal void AddDeviceProfile(string capabilityId, bool color = false)
    {
        if (!DeviceProfilesAvailable)
        {
            return;
        }

        var id = $"profile-{Guid.NewGuid():N}";
        DeviceProfileRowViewModel row = new(new DeviceAuthoredProfile
        {
            ProfileId = id,
            Name = $"Profile {DeviceProfiles.Count + 1}",
            CapabilityId = capabilityId,
            // One or the other, never both: the capability being authored decides which, and a
            // profile carrying an unused half would let a capability change silently resurrect a
            // value the user set for something else.
            Curve = color
                ? []
                :
                [
                    new AuthoredCurvePoint { Input = 0, Output = 0 },
                    new AuthoredCurvePoint { Input = 100, Output = 100 }
                ],
            Color = color ? (int)(AccentPalette.Parse(AppConfig.DefaultAccentColor).ToUInt32() & 0xFFFFFF) : null
        });
        DeviceProfiles.Add(row);
        SelectedDeviceProfile = row;
        _deviceProfilesEdited = true;
    }

    /// <summary>Removes the selected profile.</summary>
    internal void RemoveSelectedDeviceProfile()
    {
        if (_selectedDeviceProfile is not { } row)
        {
            return;
        }

        var index = DeviceProfiles.IndexOf(row);
        DeviceProfiles.Remove(row);
        _deviceProfilesEdited = true;
        SelectedDeviceProfile = DeviceProfiles.Count == 0
            ? null
            : DeviceProfiles[Math.Min(index, DeviceProfiles.Count - 1)];
    }

    /// <summary>Records that a profile changed.</summary>
    internal void NoteDeviceProfileEdited()
    {
        _deviceProfilesEdited = true;
    }

    private void LoadDeviceProfiles(HandheldDefinition? definition)
    {
        _deviceProfileDefinition = definition?.Id ?? string.Empty;
        _deviceProfileFamily = definition?.FamilyId ?? string.Empty;
        DeviceProfiles.Clear();
        var scope = _config.DeviceIntegration.DeviceProfiles.FirstOrDefault(candidate =>
            candidate.DeviceDefinitionId == _deviceProfileDefinition && candidate.FamilyId == _deviceProfileFamily);
        foreach (var profile in scope?.Profiles ?? [])
        {
            DeviceProfiles.Add(new DeviceProfileRowViewModel(profile));
        }

        SelectedDeviceProfile = DeviceProfiles.FirstOrDefault();
        _deviceProfilesEdited = false;
        Raise(nameof(DeviceProfilesAvailable));
    }
}
