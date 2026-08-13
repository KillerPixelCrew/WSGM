using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>Lists every program currently holding a power request, grouped by the kind
/// of lock it holds — the full detail behind the Keep Awake row's indicator dot and its
/// one-line summary.
///
/// <para>Enumerating system-wide power requests needs an elevated token (a Windows
/// restriction that <c>powercfg /requests</c> shares), so an unelevated WSGM shows the
/// reason rather than an empty list that would read as "nothing is holding a
/// lock".</para></summary>
public sealed class WakeLockHoldersView : OverlaySubView
{
    /// <inheritdoc />
    protected override string LogScope => "Wake locks";

    /// <summary>Queries the current power requests and renders them.</summary>
    public void Open()
    {
        _stack.Clear();
        _current = null;
        _ = RunSafelyAsync(RenderAsync(), "holder list");
    }

    private async Task RenderAsync()
    {
        Navigate(() => RenderLoading("What's keeping this awake"));
        var generation = _navigationGeneration;
        // The syscall is fast (~65 µs) but the decode walks the whole list; keep it
        // off the UI thread like every other blocking call the overlay makes.
        var snapshot = await Task.Run(PowerRequestList.Query);
        if (generation != _navigationGeneration)
        {
            return;
        }
        Replace(() => RenderList(snapshot.Entries is null, snapshot.Error,
            WakeLockHolders.Build(snapshot.Entries)));
    }

    private void RenderList(
        bool unknown, string? error,
        System.Collections.Generic.IReadOnlyList<WakeLockHolderGroup> groups)
    {
        var stack = NewStack("What's keeping this awake");
        if (unknown)
        {
            stack.Children.Add(Caption(
                "Couldn't read the power requests" + (error is null ? "." : $": {error}")));
        }
        else if (groups.Count == 0)
        {
            stack.Children.Add(Caption(
                "Nothing is holding a lock — the screen and standby are both free."));
        }
        else
        {
            foreach (var group in groups)
            {
                stack.Children.Add(SectionLabel(group.Title));
                foreach (var holder in group.Holders)
                {
                    var title = holder.Count > 1 ? $"{holder.Label} ×{holder.Count}" : holder.Label;
                    var detail = holder.Reason is null
                        ? holder.Detail
                        : $"{holder.Detail} — {holder.Reason}";
                    // Informational rows: no click target, so gamepad focus walks
                    // straight down the list to Refresh and Back.
                    stack.Children.Add(Row(title, detail, Icons.ListLines, null));
                }
            }
        }
        stack.Children.Add(SectionLabel(""));
        stack.Children.Add(Row("Refresh", "Read the power requests again", Icons.Restart, Open));
        stack.Children.Add(Row("Back", "", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }
}
