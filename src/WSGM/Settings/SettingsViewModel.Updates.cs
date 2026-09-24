using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    private UpdateOffer? _updateOffer;

    /// <summary>Gets or sets whether WSGM checks GitHub once a day for a newer release.</summary>
    public bool CheckForUpdates
    {
        get;
        set => SetField(ref field, value, nameof(CheckForUpdates));
    }

    /// <summary>What the last update check found.</summary>
    public string UpdateStatusText
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(UpdateStatusText));
    } = "";

    /// <summary>Installed community plugins the newer release no longer carries, with who to ask.</summary>
    public string UpdateWarningText
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(UpdateWarningText));
    } = "";

    /// <summary>Whether a newer release is known.</summary>
    public bool UpdateAvailable => _updateOffer is not null;

    /// <summary>Whether the newer release would stop a community plugin from loading.</summary>
    public bool HasUpdateWarning => UpdateWarningText.Length > 0;

    /// <summary>Whether the user pressed Update and is being asked to confirm.</summary>
    public bool UpdateConfirmPending
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(UpdateConfirmPending));
    }

    /// <summary>Whether a check or download is running.</summary>
    public bool UpdateBusy
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(UpdateBusy));
    }

    /// <summary>Checks for a newer release now.</summary>
    public AsyncRelayCommand CheckForUpdatesNowCommand => field ??= new AsyncRelayCommand(CheckForUpdatesNowAsync);

    /// <summary>Asks for confirmation before the update closes Steam and WSGM.</summary>
    public RelayCommand RequestUpdateCommand => field ??= new RelayCommand(() => UpdateConfirmPending = UpdateAvailable);

    /// <summary>Withdraws the update request.</summary>
    public RelayCommand CancelUpdateCommand => field ??= new RelayCommand(() => UpdateConfirmPending = false);

    /// <summary>Downloads the release's setup, verifies it and runs its quiet update.</summary>
    public AsyncRelayCommand ApplyUpdateCommand => field ??= new AsyncRelayCommand(ApplyUpdateAsync);

    private void LoadUpdateState()
    {
        ShowUpdateState(_services.ReadUpdates?.Invoke() ?? new UpdateState());
    }

    private void ShowUpdateState(UpdateState state)
    {
        _updateOffer = state.Offer is { } offer && UpdateChecker.IsNewer(offer.Release.Version, UpdateChecker.CurrentVersion)
            ? offer
            : null;
        var checkedText = state.LastCheckUtc is { } last
            ? $"Last checked {last.ToLocalTime():g}."
            : "Not checked yet.";
        UpdateStatusText = _updateOffer is { } found
            ? $"WSGM {found.Release.Version} is available. You have {UpdateChecker.CurrentVersion}. {checkedText}"
            : $"WSGM {UpdateChecker.CurrentVersion} is current. {checkedText}";
        UpdateWarningText = _updateOffer is { Warnings.Count: > 0 } warned
            ? "These plugins did not build for the new version and will stop loading after the update. "
              + "Staying on this version until they are updated is recommended: "
              + string.Join("; ", warned.Warnings.Select(warning =>
                  warning.Contact is { Length: > 0 } contact ? $"{warning.Name} (ask {contact})" : warning.Name))
              + "."
            : "";
        UpdateConfirmPending = false;
        Raise(nameof(UpdateAvailable));
        Raise(nameof(HasUpdateWarning));
    }

    private async Task CheckForUpdatesNowAsync()
    {
        UpdateBusy = true;
        try
        {
            UpdateStatusText = "Checking for updates…";
            using var http = UpdateChecker.CreateHttpClient();
            ShowUpdateState(await Task.Run(() => UpdateChecker.CheckAsync(http, CancellationToken.None)));
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateOffer is not { } offer)
        {
            return;
        }

        UpdateConfirmPending = false;
        UpdateBusy = true;
        try
        {
            UpdateStatusText = $"Downloading WSGM {offer.Release.Version}…";
            Progress<double> progress = new(fraction =>
                UpdateStatusText = $"Downloading WSGM {offer.Release.Version}… {fraction:P0}");
            using var http = UpdateChecker.CreateHttpClient();
            var setup = await Task.Run(() => UpdateChecker.DownloadAsync(http, offer.Release, progress, CancellationToken.None));
            UpdateStatusText = "Starting the update. Steam and WSGM close now and WSGM starts again when it is done.";
            UpdateChecker.RunSetup(setup);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException
                                       or UnauthorizedAccessException or System.IO.InvalidDataException
                                       or System.ComponentModel.Win32Exception)
        {
            Log.Warn("Update: " + ex.Message);
            UpdateStatusText = "The update did not start: " + ex.Message;
        }
        finally
        {
            UpdateBusy = false;
        }
    }
}
