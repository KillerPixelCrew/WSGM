using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The header's settings-profile switch: Global or Per-application for the running application,
///     with the profile name and override count above it.
/// </summary>
/// <remarks>
///     The switch writes through the shared performance projection, the same backend as Steam's
///     per-game toggle. While a write runs the switch is disabled; a failed write leaves the
///     reason in the switch's tooltip until the next refresh.
/// </remarks>
internal sealed class HeaderProfileScope
{
    private readonly ComboBox _combo;
    private readonly TextBlock _context;
    private readonly CancellationToken _lifetime;
    private readonly Button _manage;
    private bool _closed;
    private PerformanceOverlayBridge? _source;
    private bool _synchronizing;
    private string? _target;
    private bool _writing;

    /// <summary>Takes over the header's profile controls.</summary>
    /// <param name="combo">The Global / Per-application selector.</param>
    /// <param name="context">The caption naming the profile above the selector.</param>
    /// <param name="manage">The button that opens the application profiles editor.</param>
    /// <param name="lifetime">Cancelled when the window closes, ending a pending write.</param>
    internal HeaderProfileScope(ComboBox combo, TextBlock context, Button manage, CancellationToken lifetime)
    {
        _combo = combo;
        _context = context;
        _manage = manage;
        _lifetime = lifetime;
        _combo.SelectionChanged += OnSelectionChanged;
        _combo.DropDownClosed += OnDropDownClosed;
    }

    /// <summary>Follows another performance projection, or none, from the next <see cref="Refresh" /> on.</summary>
    /// <param name="source">The borrowed projection, or null without one.</param>
    internal void Attach(PerformanceOverlayBridge? source)
    {
        _source = source;
    }

    /// <summary>Shows the projection's current scope, profile name and override count.</summary>
    internal void Refresh()
    {
        var scope = _source?.ProfileScope;
        var target = scope?.Target;
        _manage.IsEnabled = _source is not null;
        _synchronizing = true;
        try
        {
            if (_target != target?.ApplicationId)
            {
                _combo.IsDropDownOpen = false;
            }

            _target = target?.ApplicationId;
            if (!_combo.IsDropDownOpen)
            {
                _combo.SelectedIndex = scope?.Enabled == true ? 1 : 0;
            }

            _combo.IsEnabled = target is not null && !_writing;
            var snapshot = _source?.ProfileSnapshot;
            var matched = snapshot?.Game;
            var name = matched is { Name.Length: > 0 }
                ? matched.Name
                : target?.RtssProfileName ?? target?.ApplicationId;
            // The count is what the header is glanced at mid-game for: how much of what is running
            // differs from Global.
            var overrides = _source?.GameOverrideCount ?? 0;
            _context.Text = name is null
                ? "Profile"
                : snapshot is { EditsGame: true }
                    ? $"Profile: {name} · {overrides} {(overrides == 1 ? "override" : "overrides")}"
                    : "Profile: " + name;
            ToolTip.SetTip(_combo, name is null
                ? "Start or focus an application to give it separate settings."
                : $"Settings profile for {name}. Global uses shared values; Per-application stores only what you "
                  + "change while it is on, and everything else still comes from Global.");
        }
        finally
        {
            _synchronizing = false;
        }
    }

    /// <summary>Stops committing changes as the window closes.</summary>
    internal void Detach()
    {
        _closed = true;
    }

    private async void OnDropDownClosed(object? sender, EventArgs e)
    {
        await CommitAsync();
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_source is not null && !_combo.IsDropDownOpen)
        {
            await CommitAsync();
        }
    }

    private async Task CommitAsync()
    {
        if (_synchronizing || _writing || _closed || _source is not { } source || _target is not { } target)
        {
            return;
        }

        var enabled = _combo.SelectedIndex == 1;
        if (source.ProfileScope.Target?.ApplicationId != target || source.ProfileScope.Enabled == enabled)
        {
            Refresh();
            return;
        }

        _writing = true;
        _combo.IsEnabled = false;
        string? failure = null;
        try
        {
            await source.SetProfileScopeAsync(target, enabled, _lifetime);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = "Profile change was not confirmed: " + ex.Message;
            Log.Warn(failure);
        }
        finally
        {
            _writing = false;
            if (!_closed)
            {
                Refresh();
                if (failure is not null)
                {
                    ToolTip.SetTip(_combo, failure);
                }
            }
        }
    }
}
