using System;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One shared presentation for a page action and its home pin, with a borrowed cached application icon.</summary>
internal sealed class ProgramActionRow : IconActionRow
{
    internal ProgramActionRow(ProgramState state, Action activate) : base(activate)
    {
        Apply(state);
    }

    internal void Apply(ProgramState state)
    {
        Title = state.Definition.Name;
        Description = state.Status;
        TrailingText = state.Running ? "Focus" : state.CanLaunch ? "Launch" : "Review";
        ShowIcon(state.Icon);
    }
}
