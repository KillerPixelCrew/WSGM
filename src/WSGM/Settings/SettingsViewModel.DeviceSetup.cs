using System;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>Gets or sets the optional production Device Integration master switch.</summary>
    public bool DeviceIntegrationEnabled
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceIntegrationEnabled));
        }
    }

    /// <summary>Gets or sets the remembered controller-management child preference.</summary>
    public bool DeviceControllerManagementEnabled
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceControllerManagementEnabled));
        }
    }

    /// <summary>Gets or sets whether guide button chord edits are kept for a Steam Deck target.</summary>
    /// <remarks>Only this window edits it, so it is written on every save.</remarks>
    public bool DeviceKeepGuideChordEdits
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceKeepGuideChordEdits));
        }
    }

    /// <summary>Gets or sets whether AutoTDP controls the primary power limit.</summary>
    /// <remarks>
    ///     One of the three device settings the running shell also owns: the overlay and the native
    ///     quick-access menu persist all of them while this window is open. The shared-field table tracks
    ///     whether each was edited here, because a save merges over a fresh load and an untouched snapshot would
    ///     otherwise revert whatever the running session had changed. See <see cref="DeviceEditsMade" />.
    /// </remarks>
    public bool DeviceAutoTdpEnabled
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceAutoTdpEnabled));
        }
    }

    /// <summary>Selected global managed-controller target index.</summary>
    public int DeviceControllerTargetIndex
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceControllerTargetIndex));
        }
    }

    /// <summary>Selected physical glyph-policy index.</summary>
    public int DeviceGlyphSelectionIndex
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceGlyphSelectionIndex));
        }
    }

    /// <summary>Which runtime-owned device settings this window actually edited.</summary>
    /// <remarks>Measured through the shared-field table against the last loaded or captured baseline.</remarks>
    internal (bool AutoTdp, bool ControllerTarget, bool GlyphSelection) DeviceEditsMade
    {
        get
        {
            var edits = CaptureSaveRequest().SharedEdits;
            return (edits.Contains("DeviceIntegration.AutoTdpEnabled"),
                edits.Contains("Profiles.Global.ControllerTarget"), edits.Contains("DeviceIntegration.GlyphSelection"));
        }
    }

    /// <summary>Read-only status reported by the authoritative shell coordinator.</summary>
    public string DeviceOwnerStatusText
    {
        get;
        private set => SetField(ref field, value, nameof(DeviceOwnerStatusText));
    } = "No running device coordinator detected.";

    /// <summary>Refreshes the read-only owner snapshot without creating a device cycle.</summary>
    public async Task RefreshDeviceOwnerStatusAsync()
    {
        try
        {
            var snapshot =
                await DeviceCoordinatorDiagnosticsClient.TryReadAsync(
                    (uint)WindowFinder.CurrentSessionId,
                    TimeSpan.FromMilliseconds(750));
            DeviceOwnerStatusText = snapshot is null
                ? "No running device coordinator detected. Saved changes apply at the next shell start."
                : $"{snapshot.State} · {snapshot.InstalledPackage?.PackageId ?? "no package"} · "
                  + $"{snapshot.HealthyCapabilityCount}/{snapshot.CapabilityCount} healthy · "
                  + $"cycle {snapshot.CycleGeneration}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Device owner status refresh failed: {ex.Message}");
            DeviceOwnerStatusText = $"Could not read the running device owner: {ex.Message}";
        }
    }
}
