using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>Projects manual power mode; selection saves policy without applying a wattage.</summary>
/// <remarks>Reads on attach; the owning window calls <see cref="Refresh" /> when the device changes.</remarks>
internal sealed class ManualTdpModeView : StackPanel
{
    private readonly ComboBox _choice = new() { ItemsSource = new[] { "Advanced / split TDP", "Unified TDP" } };
    private readonly ProfileOverrideMarker _marker;
    private readonly Func<string?>? _overrideId;
    private readonly Func<(bool Available, bool Unified)> _read;
    private readonly Func<bool, Task> _save;

    private readonly TextBlock _status = new()
    {
        Text = "Unified TDP coordinates sustained and boost limits through the device plugin.",
        TextWrapping = TextWrapping.Wrap
    };

    private bool _closed;
    private bool _rendering;
    private bool _writing;

    internal ManualTdpModeView(Func<(bool Available, bool Unified)> read, Func<bool, Task> save,
        Func<string?>? overrideId = null, Func<string, Task>? useGlobal = null)
    {
        _read = read;
        _save = save;
        _overrideId = overrideId;
        Spacing = 6;
        _marker = new ProfileOverrideMarker(_choice, useGlobal);
        Children.Add(_marker);
        Children.Add(_status);
        // Browsing an open dropdown must not save every mode it passes.
        ComboCommit.Attach<string>(_choice, () => _rendering || _writing || _closed, selected => _ = SaveAsync());
        AttachedToVisualTree += (_, _) =>
        {
            _closed = false;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => _closed = true;
    }

    /// <summary>Shows the current mode and its game override, unless a save is in flight.</summary>
    internal void Refresh()
    {
        if (_writing || _closed)
        {
            return;
        }

        var state = _read();
        _rendering = true;
        IsVisible = state.Available;
        _choice.IsEnabled = state.Available;
        // A refresh must not move the selection the user is browsing.
        if (!_choice.IsDropDownOpen)
        {
            _choice.SelectedIndex = state.Unified ? 1 : 0;
        }

        _marker.Refresh(_overrideId?.Invoke());
        _rendering = false;
    }

    private async Task SaveAsync()
    {
        _writing = true;
        _choice.IsEnabled = false;
        try
        {
            await _save(_choice.SelectedIndex == 1);
            _status.Text = "Mode saved. Adjust the sustained TDP slider to apply a target.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _writing = false;
            Refresh();
        }
    }
}
