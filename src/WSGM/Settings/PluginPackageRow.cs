using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>One package on the Plugins page: installed, available from the release, or refused.</summary>
public sealed class PluginPackageRow : ObservableObject
{
    private string _notice;

    internal PluginPackageRow(PluginPackageRowState state, Func<PluginPackageRowState, Task<string>> act)
    {
        State = state;
        _notice = state.Notice;
        ActCommand = new AsyncRelayCommand(async () => Notice = await act(State));
    }

    internal PluginPackageRowState State { get; }

    /// <summary>Display name.</summary>
    public string Name => State.Name;

    /// <summary>Version and status, for example <c>1.2.0 · Installed</c>.</summary>
    public string Detail => string.Join(" · ", new[] { State.Version, State.Status, State.Badges }
        .Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>What needs attention, or what the last action did.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (_notice == value)
            {
                return;
            }

            _notice = value;
            Raise(nameof(Notice));
            Raise(nameof(HasNotice));
        }
    }

    /// <summary>Whether there is a notice to show.</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>The button label, or empty when the row offers nothing.</summary>
    public string ActionLabel => State.Action switch
    {
        PluginPackageAction.Install => "Install",
        PluginPackageAction.Remove => "Remove",
        _ => ""
    };

    /// <summary>Whether the row offers an action.</summary>
    public bool HasAction => State.Action is not PluginPackageAction.None;

    /// <summary>Runs the row's action.</summary>
    public ICommand ActCommand { get; }
}
