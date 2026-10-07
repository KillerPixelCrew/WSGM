using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WSGM.Controls;

namespace WSGM.Overlay;

public sealed partial class GameLibraryView
{
    private Func<List<string>, Task>? _argumentAccept;
    private List<string> _argumentDraft = [];

    private void OpenRomArguments(IReadOnlyList<string> arguments, Func<List<string>, Task> accept)
    {
        _argumentDraft = [.. arguments];
        _argumentAccept = accept;
        Navigate(RenderRomArguments);
    }

    private void RenderRomArguments()
    {
        var body = NewStack("Launch arguments");
        body.Children.Add(Caption("Each row is one argument, including spaces inside it. Use {rom} for the game path. "
                                  + "RetroArch also supports {core}, {data} and {config}. Leave the list empty to use the library or emulator default."));
        for (var index = 0; index < _argumentDraft.Count; index++)
        {
            var position = index;
            body.Children.Add(Tagged(Row("Argument " + (index + 1), _argumentDraft[index], Icons.ListLines, () =>
                EditText("Argument " + (position + 1), _argumentDraft[position], 0, value =>
                {
                    _argumentDraft[position] = value;
                    RenderRomArguments();
                })), "argument:" + index));
            body.Children.Add(Tagged(Row("Remove argument " + (index + 1), "", Icons.Close, () =>
            {
                _argumentDraft.RemoveAt(position);
                RenderRomArguments();
            }), "argument.remove:" + index));
        }

        body.Children.Add(Tagged(Row("Add argument", "One value or flag", Icons.ListLines,
            () => EditText("New argument", "", 0, value =>
            {
                _argumentDraft.Add(value);
                RenderRomArguments();
            })), "arguments.add"));
        body.Children.Add(Tagged(Row("Use default arguments", "Clear this override", Icons.Restart, () =>
        {
            _argumentDraft.Clear();
            RenderRomArguments();
        }), "arguments.default"));
        body.Children.Add(Tagged(PrimaryRow("Save arguments", "Apply this choice to the title or staged library",
            Icons.Play, () => _ = RunSafelyAsync(SaveRomArgumentsAsync(), "save arguments")), "arguments.save"));
        SetContent(body);
    }

    private Task SaveRomArgumentsAsync()
    {
        return _argumentAccept?.Invoke(_argumentDraft.Where(value => !string.IsNullOrWhiteSpace(value)).ToList())
               ?? Task.CompletedTask;
    }
}
