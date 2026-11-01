using WSGM.Core;
using WSGM.Shell;

namespace WSGM.UiTests.Fakes;

/// <summary>Inert hotkeys for captures and interaction tests; records commands and never sends keys.</summary>
internal sealed class HotkeysOverlaySource : IHotkeysSource
{
    private List<HotkeyState> _states;

    /// <summary>Creates the source with the populated or the empty list.</summary>
    /// <param name="empty">Whether the list starts empty.</param>
    internal HotkeysOverlaySource(bool empty = false)
    {
        _states = empty
            ? []
            :
            [
                new HotkeyState(new HotkeyDefinition
                {
                    Id = "menu", Name = "OptiScaler menu", Keys = [HotkeyKeys.Insert]
                }, ""),
                new HotkeyState(new HotkeyDefinition
                {
                    Id = "lossless", Name = "Lossless Scaling",
                    Keys = [HotkeyKeys.Control, HotkeyKeys.Alt, HotkeyKeys.S]
                }, HotkeysService.NoGameNotice, "Not sent: no game in front"),
                new HotkeyState(new HotkeyDefinition
                {
                    Id = "overlay", Name = "Performance overlay", Keys = [HotkeyKeys.Shift, 0x7A],
                    PressDelayMs = 100
                }, "")
            ];
    }

    /// <summary>Every command, as its name and argument.</summary>
    internal List<(string Command, object Argument)> Commands { get; } = [];

    public event Action? Changed;

    public HotkeyState[] ReadState()
    {
        return [.. _states.Select(state => state with { Definition = state.Definition.Copy() })];
    }

    public Task SaveAsync(HotkeyDefinition definition)
    {
        Commands.Add(("save", definition));
        var index = _states.FindIndex(state => state.Definition.Id == definition.Id);
        var state = new HotkeyState(definition.Copy(), "");
        if (index < 0)
        {
            _states.Add(state);
        }
        else
        {
            _states[index] = state;
        }

        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id)
    {
        Commands.Add(("remove", id));
        _states = [.. _states.Where(state => state.Definition.Id != id)];
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task SendAsync(string id, nint returnTo, CancellationToken cancellationToken)
    {
        Commands.Add(("send", id));
        return Task.CompletedTask;
    }

    public void ReportFailure(string id, string message)
    {
    }
}
