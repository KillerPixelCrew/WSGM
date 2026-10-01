using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private bool _profileScopeSynchronizing;
    private string? _profileScopeTarget;
    private bool _profileScopeWriting;

    private void OnManageProfiles(object? sender, RoutedEventArgs e)
    {
        if (_performanceSource is not { } source)
        {
            return;
        }

        var editor = new ApplicationProfilesView(source, _deviceLifetime.Token);
        ShowSurface(editor, SurfaceKind.Utility, "Application profiles", editor.DefaultFocusTarget);
    }

    private void RefreshHeaderProfile()
    {
        var scope = _performanceSource?.ProfileScope;
        var target = scope?.Target;
        ManageProfiles.IsEnabled = _performanceSource is not null;
        _profileScopeSynchronizing = true;
        try
        {
            if (_profileScopeTarget != target?.ApplicationId)
            {
                HeaderProfile.IsDropDownOpen = false;
            }

            _profileScopeTarget = target?.ApplicationId;
            if (!HeaderProfile.IsDropDownOpen)
            {
                HeaderProfile.SelectedIndex = scope?.Enabled == true ? 1 : 0;
            }

            HeaderProfile.IsEnabled = target is not null && !_profileScopeWriting;
            var snapshot = _performanceSource?.ProfileSnapshot;
            var matched = snapshot?.Game;
            var name = matched is { Name.Length: > 0 }
                ? matched.Name
                : target?.RtssProfileName ?? target?.ApplicationId;
            // The count is what the header is glanced at mid-game for: how much of what is running
            // differs from Global.
            var overrides = snapshot is { EditsGame: true } ? snapshot.Layers.GameOverrideCount : 0;
            ProfileContext.Text = name is null
                ? "Profile"
                : snapshot is { EditsGame: true }
                    ? $"Profile: {name} · {overrides} {(overrides == 1 ? "override" : "overrides")}"
                    : "Profile: " + name;
            ToolTip.SetTip(HeaderProfile, name is null
                ? "Start or focus an application to give it separate settings."
                : $"Settings profile for {name}. Global uses shared values; Per-application stores only what you "
                  + "change while it is on, and everything else still comes from Global.");
        }
        finally
        {
            _profileScopeSynchronizing = false;
        }
    }

    private async void OnHeaderProfileClosed(object? sender, EventArgs e)
    {
        await CommitHeaderProfileAsync();
    }

    private async void OnHeaderProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_performanceSource is not null && sender is ComboBox { IsDropDownOpen: false })
        {
            await CommitHeaderProfileAsync();
        }
    }

    private async Task CommitHeaderProfileAsync()
    {
        if (_profileScopeSynchronizing || _profileScopeWriting || _closed
            || _performanceSource is not { } source || _profileScopeTarget is not { } target)
        {
            return;
        }

        var enabled = HeaderProfile.SelectedIndex == 1;
        if (source.ProfileScope.Target?.ApplicationId != target || source.ProfileScope.Enabled == enabled)
        {
            RefreshHeaderProfile();
            return;
        }

        _profileScopeWriting = true;
        HeaderProfile.IsEnabled = false;
        string? failure = null;
        try
        {
            await source.SetProfileScopeAsync(target, enabled, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = "Profile change was not confirmed: " + ex.Message;
            Log.Warn(failure);
        }
        finally
        {
            _profileScopeWriting = false;
            if (!_closed)
            {
                RefreshHeaderProfile();
                if (failure is not null)
                {
                    ToolTip.SetTip(HeaderProfile, failure);
                }
            }
        }
    }

    private void OnRadioTileClicked(object? sender, RoutedEventArgs e)
    {
        // The panel stays in this window's focus scope and navigation owner.
        RadioPanelRequested?.Invoke((sender as Control)?.Tag as string == "bluetooth");
    }

    private void OnAudioTileClicked(object? sender, RoutedEventArgs e)
    {
        AudioPanelRequested?.Invoke();
    }

    private void OnEjectTileClicked(object? sender, RoutedEventArgs e)
    {
        EjectPanelRequested?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
    }
}
