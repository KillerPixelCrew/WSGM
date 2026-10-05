using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Install;

namespace WSGM.Settings;

/// <summary>What the Plugins page shows: the installed release's bundle, when readable, and every card.</summary>
/// <param name="Catalog">The installed packages the cards were read from.</param>
/// <param name="Adapters">The display adapters read for this page, for offers and default enablement alike.</param>
/// <param name="Bundle">The installed release's bundle, or null when it is missing or unreadable.</param>
/// <param name="Rows">The cards, each naming its section.</param>
internal sealed record PluginPackagePage(
    PluginPackageCatalog Catalog,
    IReadOnlyList<DisplayAdapterIdentity> Adapters,
    BundleManifest? Bundle,
    IReadOnlyList<PluginPackageRowState> Rows);

/// <summary>One badge on a plugin card, with its tone as flags the page's badge classes bind to.</summary>
public sealed class PluginBadgeView(PluginBadge badge)
{
    /// <summary>What the badge says.</summary>
    public string Text { get; } = badge.Text;

    /// <summary>First-party.</summary>
    public bool IsAccent { get; } = badge.Tone is PluginBadgeTone.Accent;

    /// <summary>Community.</summary>
    public bool IsCommunity { get; } = badge.Tone is PluginBadgeTone.Community;

    /// <summary>Available.</summary>
    public bool IsInfo { get; } = badge.Tone is PluginBadgeTone.Info;

    /// <summary>Installed, hardware-tested.</summary>
    public bool IsGood { get; } = badge.Tone is PluginBadgeTone.Good;

    /// <summary>Blind, removing.</summary>
    public bool IsWarn { get; } = badge.Tone is PluginBadgeTone.Warn;

    /// <summary>Refused, outdated.</summary>
    public bool IsBad { get; } = badge.Tone is PluginBadgeTone.Bad;
}

/// <summary>One plugin card on the Plugins page: installed, available from the release, or unavailable.</summary>
public sealed class PluginPackageRow : ObservableObject
{
    private string _notice;

    internal PluginPackageRow(PluginPackageRowState state, Func<PluginPackageRowState, Task<string>> act)
    {
        State = state;
        _notice = state.Notice;
        Badges = [.. state.Badges.Select(badge => new PluginBadgeView(badge))];
        ActCommand = new AsyncRelayCommand(async () => Notice = await act(State));
    }

    internal PluginPackageRowState State { get; }

    /// <summary>Display name.</summary>
    public string Name => State.Name;

    /// <summary>Status, version, kind, origin and validation.</summary>
    public IReadOnlyList<PluginBadgeView> Badges { get; }

    /// <summary>A gamepad for a device plugin, a wrench for an integration.</summary>
    public StreamGeometry Icon => State.IsDevice ? Icons.SteamLike : Icons.Wrench;

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

    /// <summary>Whether the notice reports a problem rather than a fact, so it is shown in the warning colour.</summary>
    public bool NoticeIsProblem => State.Badges.Any(badge => badge.Tone is PluginBadgeTone.Bad);

    /// <summary>The button label, or empty when the row offers nothing.</summary>
    public string ActionLabel => State.Action switch
    {
        PluginPackageAction.Install => "Install",
        PluginPackageAction.Remove => "Remove",
        _ => ""
    };

    /// <summary>Whether the row offers an action.</summary>
    public bool HasAction => State.Action is not PluginPackageAction.None;

    /// <summary>Whether the action installs, which the page shows as the accent button.</summary>
    public bool IsInstall => State.Action is PluginPackageAction.Install;

    /// <summary>Runs the row's action.</summary>
    public ICommand ActCommand { get; }
}
